using System.Text;
using System.Threading.Channels;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Wraps a <see cref="Conversation"/> on a shared <see cref="BatchedExecutor"/>.
/// Provides the SAME surface and behavior as <see cref="SessionContext"/>.
///
/// <para><b>Buffer system:</b> Sessions NEVER touch their Conversation directly.
/// Instead they buffer prompts and mutations (compact, reset, save) locally.
/// The <see cref="BatchInferenceCoordinator"/> collects all buffers, applies mutations,
/// flushes prompts, then runs ONE Infer(). This means:</para>
///
/// <list type="bullet">
/// <item>Session A can compact while session B is mid-generation — A buffers a
///   Reset mutation. The coordinator applies it before the next Infer() cycle.</item>
/// <item>Multiple compactions can happen in one cycle — the coordinator processes
///   them sequentially before Infer().</item>
/// <item>No locking — sessions write to their own buffers, coordinator reads them.</item>
/// <item>The coordinator is the ONLY thread that touches conversations.</item>
/// </list>
/// </summary>
public sealed class BatchSession : IDisposable
{
    private readonly BatchInferenceCoordinator _coordinator;
    private readonly MtmdWeights? _mtmd;
    private readonly ILogger _logger;
    private bool _disposed;

    // The actual conversation — ONLY touched by the coordinator thread
    private Conversation _conversation;

    // ── Buffers (session writes, coordinator reads) ──
    // Pending prompt text — buffered by InferAsync/PrefillAsync, flushed by coordinator
    private string? _pendingPrompt;
    // Pending mutation — buffered by SaveStateAsync/RewindAsync/ResetAsync, applied by coordinator
    private PendingMutation? _pendingMutation;
    // Saved state for rewind
    private Conversation.State? _savedState;
    // Bookkeeping
    private int _approxTokenCount;
    private bool _isPrefilled;

    public string Key { get; }
    public string ClientId { get; }
    public string SessionId { get; }
    public string ModelId { get; }
    public uint ContextSize => _coordinator.Context.ContextSize;
    public int ApproxTokenCount => _approxTokenCount;
    public bool IsPrefilled => _isPrefilled;
    public bool HasSavedState => _savedState != null;
    public double EstimatedVramMb => 0;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string? ToolsHash { get; set; }

    public BatchSession(
        string clientId,
        string sessionId,
        string modelId,
        BatchInferenceCoordinator coordinator,
        ILogger logger)
    {
        ClientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        Key = $"{clientId}:{sessionId}";
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mtmd = coordinator.Executor.ClipModel;
        _conversation = coordinator.CreateConversation();
        _coordinator.Register(this);
    }

    // ── Buffer accessors (called by coordinator) ──

    /// <summary>Coordinator calls this to take the pending prompt. Returns null if none.</summary>
    internal string? TakePendingPrompt()
    {
        var p = _pendingPrompt;
        _pendingPrompt = null;
        return p;
    }

    /// <summary>Coordinator calls this to take the pending mutation. Returns null if none.</summary>
    internal PendingMutation? TakePendingMutation()
    {
        var m = _pendingMutation;
        _pendingMutation = null;
        return m;
    }

    /// <summary>Coordinator calls this to flush the prompt to the conversation.</summary>
    internal void ApplyPrompt(string prompt)
    {
        _conversation.Prompt(prompt);
    }

    /// <summary>Coordinator calls this to apply a save mutation.</summary>
    internal void ApplySave()
    {
        _savedState?.Dispose();
        _savedState = _conversation.Save();
    }

    /// <summary>Coordinator calls this to apply a rewind mutation.</summary>
    internal void ApplyRewind()
    {
        if (_savedState == null) return;
        try { _mtmd?.ClearMedia(); } catch { }
        var executor = _coordinator.Executor;
        _conversation.Dispose();
        _conversation = executor.Load(_savedState);
    }

    /// <summary>Coordinator calls this to apply a reset mutation.</summary>
    internal void ApplyReset()
    {
        _conversation.Dispose();
        _conversation = _coordinator.CreateConversation();
        _savedState?.Dispose();
        _savedState = null;
        _isPrefilled = false;
        _approxTokenCount = 0;
    }

    // ── Public API (same surface as SessionContext) ──

    /// <summary>
    /// Prefill the KV cache. Buffers the prompt; the coordinator flushes + infers.
    /// </summary>
    public async Task<(bool success, int tokens, long elapsedMs)> PrefillAsync(string text, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BatchSession));
        if (_isPrefilled)
            return (true, _approxTokenCount, 0);

        var startMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

        try
        {
            var promptText = ResolveMtmdMarker(text);

            if (_mtmd != null)
                _mtmd.ClearMedia();

            // Buffer the prompt — coordinator will flush + Infer
            _pendingPrompt = promptText;

            await _coordinator.RunInferCycleAsync(ct);

            _isPrefilled = true;
            _approxTokenCount = EstimateTokenCount(text);
            var elapsedMs = (long)((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - startMs);

            try { _mtmd?.ClearMedia(); } catch { }
            _logger.Info("BatchSession", $"[{Key}] Prefilled ~{_approxTokenCount} tokens in {elapsedMs}ms");
            return (true, _approxTokenCount, elapsedMs);
        }
        catch (Exception ex)
        {
            _logger.Error("BatchSession", $"[{Key}] Prefill failed: {ex.Message}");
            try { _mtmd?.ClearMedia(); } catch { }
            return (false, 0, 0);
        }
    }

    /// <summary>
    /// Infer with streaming. Buffers prompt text; coordinator flushes + Infers each cycle.
    /// Supports grammar, vision, anti-prompts, overflow — same as SessionContext.
    /// </summary>
    public async IAsyncEnumerable<string> InferAsync(
        string prompt,
        InferenceParams? inferenceParams = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        IReadOnlyList<byte[]>? images = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BatchSession));
        if (images is { Count: > 0 } && _mtmd == null)
            throw new InvalidOperationException("Model has no mmproj loaded — images are not supported on this session.");

        LastActivity = DateTime.UtcNow;

        var pipe = (inferenceParams?.SamplingPipeline as DefaultSamplingPipeline)
            ?? CreateDefaultPipeline();

        // Vision: queue media into the projector BEFORE prompting
        if (_mtmd != null && images is { Count: > 0 })
        {
            _mtmd.ClearMedia();
            foreach (var img in images)
                _mtmd.LoadMedia(img);
        }

        // Resolve MTMD marker for vision prompts
        var effectivePrompt = prompt;
        if (_mtmd != null && images is { Count: > 0 })
        {
            var mtmdMarker = GetMtmdMarker();
            effectivePrompt = prompt.Replace(
                ECAssistant.LLM.Models.ChatMessageContentConverter.DefaultImageMarker,
                mtmdMarker);

            _logger.Info("BatchSession", $"[{Key}] Vision: {images.Count} media queued");
        }

        // Overflow guard: check headroom before generation
        var maxTokens = inferenceParams?.MaxTokens ?? 512;
        var headroom = (int)ContextSize - _approxTokenCount - 16;
        if (maxTokens > headroom)
        {
            maxTokens = Math.Max(1, headroom);
            _logger.Warn("BatchSession", $"[{Key}] Clamped max_tokens to {maxTokens} (headroom={headroom})");
        }

        // Count input tokens
        if (!string.IsNullOrEmpty(effectivePrompt))
        {
            _approxTokenCount += EstimateTokenCount(effectivePrompt);
        }

        // Check for overflow BEFORE inference
        if (_approxTokenCount >= (int)ContextSize)
        {
            try { _mtmd?.ClearMedia(); } catch { }
            throw new LLama.Exceptions.ContextOverflowException(
                $"[{Key}] Context overflowed before generation (approx={_approxTokenCount}, ctx={ContextSize})");
        }

        var antiPrompts = inferenceParams?.AntiPrompts ?? Array.Empty<string>();
        var generated = 0;
        var sb = new StringBuilder();

        // Buffer the initial prompt for the first cycle
        if (!string.IsNullOrEmpty(effectivePrompt))
        {
            _pendingPrompt = effectivePrompt;
        }

        while (generated < maxTokens)
        {
            // Coordinator: apply mutations → flush prompts → Infer() → return
            DecodeResult result;
            try
            {
                result = await _coordinator.RunInferCycleAsync(ct);
            }
            catch (Exception ex) when (IsContextOverflow(ex))
            {
                try { _mtmd?.ClearMedia(); } catch { }
                throw new LLama.Exceptions.ContextOverflowException(
                    $"[{Key}] Context overflowed during generation: {ex.Message}");
            }

            if (result != DecodeResult.Ok)
            {
                _logger.Warn("BatchSession", $"[{Key}] Infer returned {result} during generation");
                break;
            }

            // Sample this conversation's token — pipeline includes grammar
            LLamaToken tokenId;
            try
            {
                tokenId = _conversation.Sample(pipe);
            }
            catch (Exception ex)
            {
                _logger.Warn("BatchSession", $"[{Key}] Sample failed: {ex.Message}");
                break;
            }

            // Check for end-of-generation
            var vocab = _coordinator.Context.Vocab;
            if (IsEndOfGeneration(vocab, tokenId))
                break;

            // Detokenize
#pragma warning disable CS0618
            var text = _coordinator.Context.DeTokenize(new[] { tokenId });
#pragma warning restore CS0618
            sb.Append(text);

            // Check anti-prompts
            if (antiPrompts.Count > 0)
            {
                var fullText = sb.ToString();
                var hitAnti = false;
                foreach (var anti in antiPrompts)
                {
                    if (fullText.Contains(anti))
                    {
                        hitAnti = true;
                        break;
                    }
                }
                if (hitAnti)
                    break;
            }

            generated++;
            yield return text;

            // Buffer the sampled token for the NEXT Infer() cycle
            // The coordinator will flush it before the next Infer()
            _pendingPrompt = text;
        }

        _approxTokenCount += EstimateTokenCount(sb.ToString());
        try { _mtmd?.ClearMedia(); } catch { }
    }

    /// <summary>
    /// Save state — buffers a Save mutation. Coordinator applies it before next Infer().
    /// </summary>
    public Task<bool> SaveStateAsync()
    {
        if (_disposed)
            return Task.FromResult(false);

        _pendingMutation = new PendingMutation { Type = PendingMutationType.Save };
        _logger.Info("BatchSession", $"[{Key}] SaveState buffered");
        return Task.FromResult(true);
    }

    /// <summary>
    /// Rewind — buffers a Rewind mutation. Coordinator applies it before next Infer().
    /// </summary>
    public Task<bool> RewindAsync()
    {
        if (_disposed || _savedState == null)
            return Task.FromResult(false);

        _pendingMutation = new PendingMutation { Type = PendingMutationType.Rewind };
        _logger.Info("BatchSession", $"[{Key}] Rewind buffered");
        return Task.FromResult(true);
    }

    /// <summary>
    /// Reset — buffers a Reset mutation. Coordinator applies it before next Infer().
    /// </summary>
    public Task ResetAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BatchSession));

        _pendingMutation = new PendingMutation { Type = PendingMutationType.Reset };
        _logger.Info("BatchSession", $"[{Key}] Reset buffered");
        return Task.CompletedTask;
    }

    /// <summary>
    /// v15 KV-hygiene — feed text into cache as PROMPT with bounded sampling.
    /// Buffers the prompt; coordinator flushes + Infers.
    /// </summary>
    public async Task<(bool success, int sampledTokens, int approxTokens)> EvaluateAsync(
        string text, int maxTokens = 1, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BatchSession));
        if (string.IsNullOrEmpty(text))
            return (true, 0, _approxTokenCount);

        try
        {
            var effectiveText = ResolveMtmdMarker(text);
            _approxTokenCount += EstimateTokenCount(text);

            // Buffer the prompt
            _pendingPrompt = effectiveText;

            var result = await _coordinator.RunInferCycleAsync(ct);
            if (result != DecodeResult.Ok)
                return (false, 0, _approxTokenCount);

            // Sample and discard up to maxTokens tokens
            var pipe = CreateDefaultPipeline();
            var sampled = 0;
            for (var i = 0; i < maxTokens; i++)
            {
                try
                {
                    var tokenId = _conversation.Sample(pipe);
                    sampled++;
                    // Buffer sampled token for next cycle if we need more
                    if (i < maxTokens - 1)
                        _pendingPrompt = _coordinator.Context.DeTokenize(new[] { tokenId });
                }
                catch
                {
                    break;
                }
            }

            _approxTokenCount += sampled;
            return (true, sampled, _approxTokenCount);
        }
        catch (Exception)
        {
            return (false, 0, _approxTokenCount);
        }
    }

    /// <summary>Get the underlying LLamaContext (for tokenization).</summary>
    public LLamaContext? GetContext() => _coordinator?.Context;

    // ── MTMD / Vision helpers ─────────────────────────

    private string ResolveMtmdMarker(string text)
    {
        if (_mtmd == null)
            return text;
        try
        {
            var marker = GetMtmdMarker();
            return text.Replace(ECAssistant.LLM.Models.ChatMessageContentConverter.DefaultImageMarker, marker);
        }
        catch
        {
            return text;
        }
    }

    private static string GetMtmdMarker()
    {
        return NativeApi.MtmdDefaultMarker() ?? "";
    }

    // ── Sampling / Generation helpers ─────────────────

    private static DefaultSamplingPipeline CreateDefaultPipeline() => new()
    {
        Temperature = 0.3f,
        TopP = 0.95f,
        TopK = 40,
        RepeatPenalty = 1.1f,
    };

    private static bool IsEndOfGeneration(object? vocab, LLamaToken tokenId)
    {
        if (vocab is SafeLlamaModelHandle.Vocabulary safeVocab)
            return tokenId.IsEndOfGeneration(safeVocab);
        return false;
    }

    private static bool IsContextOverflow(Exception ex)
    {
        var msg = ex.Message ?? "";
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            msg += " " + (inner.Message ?? "");
        return msg.Contains("Context overflowed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("native memory shifting", StringComparison.OrdinalIgnoreCase);
    }

    private static int EstimateTokenCount(string text) => text.Length / 4;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _coordinator.Unregister(this);
        try { _conversation.Dispose(); } catch { }
        _savedState?.Dispose();
        _savedState = null;
    }
}