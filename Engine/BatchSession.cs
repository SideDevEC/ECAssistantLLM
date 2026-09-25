using System.Text;
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
/// <para><b>Buffer system (concurrency-hardened, 2026-09-25 second pass):</b>
/// Sessions NEVER touch their <see cref="Conversation"/> directly. Requests enqueue
/// operations into a thread-safe <see cref="BatchOpBuffer"/> (atomic FIFO — nothing can
/// be lost or reordered), then await the coordinator cycle. The coordinator is the ONLY
/// thread that touches conversations: it dequeues each session's ops in FIFO order,
/// applies mutations/prompts, then runs ONE <c>Infer()</c>.</para>
///
/// <para><b>Per-session request gate:</b> a <see cref="SemaphoreSlim"/>(1,1) serializes
/// concurrent requests to the SAME session (prefill vs chat vs evaluate vs mutations) so
/// prompts never stack up on one conversation mid-flight — matching the standard path's
/// InferenceScheduler semantics. Acquired with <c>WaitAsync</c>: no thread is ever blocked,
/// and requests to DIFFERENT sessions still batch together in one decode.</para>
///
/// <para><b>Deferred disposal:</b> Dispose never touches the conversation on a request
/// thread. The session retires into the coordinator's graveyard; the coordinator disposes
/// retired conversations INSIDE the cycle gate, eliminating the dispose-vs-inflight-cycle
/// native crash race.</para>
/// </summary>
public sealed class BatchSession : IDisposable
{
    private readonly BatchInferenceCoordinator _coordinator;
    private readonly MtmdWeights? _mtmd;
    private readonly ILogger _logger;
    // Serializes concurrent requests to this session (same-session parity with the
    // standard path's InferenceScheduler). Held for the full streaming enumeration.
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    // The actual conversation — ONLY touched by the coordinator thread (inside the cycle gate)
    private Conversation _conversation;

    // Thread-safe FIFO op buffer — session threads enqueue, coordinator dequeues. No lost writes.
    private readonly BatchOpBuffer _ops = new();

    // Saved state for rewind — only touched inside the cycle gate (coordinator) or
    // under the request gate (HasSavedState reads are best-effort bookkeeping).
    private Conversation.State? _savedState;

    // Bookkeeping — mutated only under the request gate
    private int _approxTokenCount;
    private bool _isPrefilled;

    // The last error raised while the coordinator applied this session's op (inside the
    // cycle gate). Lets Prefill/Infer report flush failures honestly instead of lying.
    private Exception? _lastApplyError;

    private volatile bool _retired;
    private bool _conversationDisposed;

    public string Key { get; }
    public string ClientId { get; }
    public string SessionId { get; }
    public string ModelId { get; }
    public uint ContextSize => _coordinator.Context.ContextSize;
    public int ApproxTokenCount => _approxTokenCount;
    public bool IsPrefilled => _isPrefilled;
    public bool HasSavedState => _savedState != null;

    /// <summary>
    /// Per-session approximate KV footprint in the SHARED batched pool. Unlike the standard
    /// path (dedicated context per session), the batch KV pool is shared — reporting each
    /// session's token-scaled share keeps /status honest without overcounting the pool.
    /// </summary>
    public double EstimatedVramMb => Math.Round(
        2.0 * 28 * Math.Max(_approxTokenCount, 1) * 4096 * 2 / (1024.0 * 1024.0), 1);

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

    // ── Coordinator-side op application (called INSIDE the cycle gate) ──

    /// <summary>Dequeue the next buffered op. Coordinator-only (called inside the cycle gate).</summary>
    internal bool TryDequeueOp(out PendingMutation op) => _ops.TryDequeue(out op!);

    /// <summary>The coordinator this session lives on (registry uses it for cleanup cycles).</summary>
    internal BatchInferenceCoordinator Coordinator => _coordinator;

    /// <summary>Coordinator-only: dispose the conversation + saved state. Must run inside
    /// the cycle gate ( graveyard cleanup ) so it can never race an in-flight Infer().</summary>
    internal void DisposeConversationNow()
    {
        if (_conversationDisposed) return;
        _conversationDisposed = true;
        try { _conversation.Dispose(); } catch { }
        _savedState?.Dispose();
        _savedState = null;
    }

    /// <summary>
    /// Apply one dequeued operation. Runs inside the cycle gate — the only place the
    /// conversation is touched. Returns false and records <see cref="TakeLastApplyError"/>
    /// when the op failed, so the requesting session can report the failure honestly.
    /// Vision media is loaded here: the shared MtmdWeights media queue must NEVER be
    /// touched outside the gate (FIFO queue would feed this session's image into another
    /// session's prompt).
    /// </summary>
    internal bool ApplyOp(PendingMutation op)
    {
        try
        {
            switch (op.Type)
            {
                case PendingMutationType.Prompt:
                    ApplyPromptCore(op.Text ?? "", op.Images);
                    break;
                case PendingMutationType.Save:
                    _savedState?.Dispose();
                    _savedState = _conversation.Save();
                    break;
                case PendingMutationType.Rewind:
                    ApplyRewindCore();
                    break;
                case PendingMutationType.Reset:
                    ApplyResetCore();
                    break;
                default:
                    return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _lastApplyError = ex;
            return false;
        }
    }

    /// <summary>Session-side: consume the apply error recorded by the coordinator, if any.</summary>
    private Exception? TakeLastApplyError()
    {
        var e = _lastApplyError;
        _lastApplyError = null;
        return e;
    }

    private void ApplyPromptCore(string prompt, IReadOnlyList<byte[]>? images)
    {
        if (images is { Count: > 0 })
        {
            _mtmd?.ClearMedia();
            foreach (var img in images)
                _mtmd?.LoadMedia(img);
        }
        _conversation.Prompt(prompt);
        // Media is consumed FIFO at the marker during Prompt() — clear residue so it
        // can never leak into another session's prompt on a later cycle.
        if (images is { Count: > 0 })
        {
            try { _mtmd?.ClearMedia(); } catch { }
        }
    }

    private void ApplyRewindCore()
    {
        if (_savedState == null) return;
        try { _mtmd?.ClearMedia(); } catch { }
        var executor = _coordinator.Executor;
        _conversation.Dispose();
        _conversation = executor.Load(_savedState);
    }

    private void ApplyResetCore()
    {
        _conversation.Dispose();
        _conversation = _coordinator.CreateConversation();
        _savedState?.Dispose();
        _savedState = null;
        _isPrefilled = false;
        _approxTokenCount = 0;
    }

    // ── Public API (same surface as SessionContext) ──
    // ALL public entry points serialize through _requestGate — concurrent requests to the
    // same session queue up (non-blocking WaitAsync) instead of corrupting buffer ordering.

    /// <summary>
    /// Prefill the KV cache. Enqueues the prompt; the coordinator flushes + infers.
    /// Returns success=false when the prompt flush OR the decode fails (flush errors are
    /// no longer swallowed into a false success).
    /// </summary>
    public async Task<(bool success, int tokens, long elapsedMs)> PrefillAsync(string text, CancellationToken ct = default)
    {
        if (_retired)
            throw new ObjectDisposedException(nameof(BatchSession));
        if (_isPrefilled)
            return (true, _approxTokenCount, 0);

        await _requestGate.WaitAsync(ct);
        try
        {
            var startMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

            try
            {
                var promptText = ResolveMtmdMarker(text);

                _ops.EnqueuePrompt(promptText);

                var result = await _coordinator.RunInferCycleAsync(ct);
                if (result != DecodeResult.Ok)
                {
                    _logger.Error("BatchSession", $"[{Key}] Prefill Infer failed: {result}");
                    return (false, 0, 0);
                }

                // Retired while queued: the graveyard cleanup disposed the conversation
                // and our buffered prompt was never applied — must NOT report success.
                if (_retired || _conversationDisposed)
                {
                    _logger.Warn("BatchSession", $"[{Key}] Session retired during prefill — aborting");
                    return (false, 0, 0);
                }

                var applyError = TakeLastApplyError();
                if (applyError != null)
                {
                    _logger.Error("BatchSession", $"[{Key}] Prefill prompt flush failed: {applyError.Message}");
                    return (false, 0, 0);
                }

                _isPrefilled = true;
                _approxTokenCount = EstimateTokenCount(text);
                var elapsedMs = (long)((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - startMs);
                LastActivity = DateTime.UtcNow;

                _logger.Info("BatchSession", $"[{Key}] Prefilled ~{_approxTokenCount} tokens in {elapsedMs}ms");
                return (true, _approxTokenCount, elapsedMs);
            }
            catch (Exception ex)
            {
                _logger.Error("BatchSession", $"[{Key}] Prefill failed: {ex.Message}");
                return (false, 0, 0);
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    /// <summary>
    /// Infer with streaming. Enqueues the prompt; the coordinator flushes + Infers each cycle.
    /// The per-session request gate is held for the WHOLE enumeration — concurrent requests
    /// to this session wait (non-blocking) until the stream completes. Supports grammar,
    /// vision, anti-prompts, overflow — same as SessionContext.
    /// </summary>
    public async IAsyncEnumerable<string> InferAsync(
        string prompt,
        InferenceParams? inferenceParams = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        IReadOnlyList<byte[]>? images = null)
    {
        if (_retired)
            throw new ObjectDisposedException(nameof(BatchSession));
        if (images is { Count: > 0 } && _mtmd == null)
            throw new InvalidOperationException("Model has no mmproj loaded — images are not supported on this session.");

        await _requestGate.WaitAsync(ct);
        try
        {
            await foreach (var token in InferCoreAsync(prompt, inferenceParams, ct, images))
                yield return token;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async IAsyncEnumerable<string> InferCoreAsync(
        string prompt,
        InferenceParams? inferenceParams,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        IReadOnlyList<byte[]>? images)
    {
        LastActivity = DateTime.UtcNow;

        var pipe = (inferenceParams?.SamplingPipeline as DefaultSamplingPipeline)
            ?? CreateDefaultPipeline();

        // Vision: media rides inside the buffered prompt op — the coordinator loads it
        // INSIDE the cycle gate. Never touch the shared MtmdWeights on a request thread.
        var effectivePrompt = prompt;
        if (_mtmd != null && images is { Count: > 0 })
        {
            var mtmdMarker = GetMtmdMarker();
            effectivePrompt = prompt.Replace(
                ECAssistant.LLM.Models.ChatMessageContentConverter.DefaultImageMarker,
                mtmdMarker);

            _logger.Info("BatchSession", $"[{Key}] Vision: {images.Count} media buffered");
        }

        // Count input tokens BEFORE the headroom clamp so the clamp sees the full
        // footprint (prompt + existing context), not just the pre-prompt remainder.
        if (!string.IsNullOrEmpty(effectivePrompt))
        {
            _approxTokenCount += EstimateTokenCount(effectivePrompt);
        }

        // Overflow guard: check headroom before generation
        var maxTokens = inferenceParams?.MaxTokens ?? 512;
        var headroom = (int)ContextSize - _approxTokenCount - 16;
        if (maxTokens > headroom)
        {
            maxTokens = Math.Max(1, headroom);
            _logger.Warn("BatchSession", $"[{Key}] Clamped max_tokens to {maxTokens} (headroom={headroom})");
        }

        // Check for overflow BEFORE inference
        if (_approxTokenCount >= (int)ContextSize)
        {
            throw new LLama.Exceptions.ContextOverflowException(
                $"[{Key}] Context overflowed before generation (approx={_approxTokenCount}, ctx={ContextSize})");
        }

        var antiPrompts = inferenceParams?.AntiPrompts ?? Array.Empty<string>();
        var generated = 0;
        var sb = new StringBuilder();

        // Enqueue the initial prompt for the first cycle
        if (!string.IsNullOrEmpty(effectivePrompt))
        {
            _ops.EnqueuePrompt(effectivePrompt, images);
        }

        while (generated < maxTokens)
        {
            // Retired/disposed mid-stream (disconnect raced a live generation): abort the
            // loop — the graveyard cleanup may have disposed this conversation inside the
            // cycle gate, so Sample() on it would be a native crash.
            if (_retired || _conversationDisposed)
            {
                _logger.Warn("BatchSession", $"[{Key}] Session retired mid-generation — aborting stream");
                break;
            }

            // Coordinator: dispose graveyard → apply ops FIFO → Infer() → return
            DecodeResult result;
            try
            {
                result = await _coordinator.RunInferCycleAsync(ct);
            }
            catch (Exception ex) when (IsContextOverflow(ex))
            {
                throw new LLama.Exceptions.ContextOverflowException(
                    $"[{Key}] Context overflowed during generation: {ex.Message}");
            }

            // Re-check: graveyard cleanup may have disposed this conversation while we
            // waited on the cycle gate.
            if (_conversationDisposed || _retired)
            {
                _logger.Warn("BatchSession", $"[{Key}] Session retired while awaiting cycle — aborting stream");
                break;
            }

            var applyError = TakeLastApplyError();
            if (applyError != null)
            {
                _logger.Error("BatchSession", $"[{Key}] Prompt flush failed during generation: {applyError.Message}");
                throw new InvalidOperationException($"[{Key}] Prompt flush failed: {applyError.Message}", applyError);
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

            // Enqueue the sampled token for the NEXT Infer() cycle (FIFO — cannot be lost)
            _ops.EnqueuePrompt(text);
        }

        _approxTokenCount += EstimateTokenCount(sb.ToString());
    }

    /// <summary>
    /// Save state — enqueues a Save op. Coordinator applies it (inside the gate) before the next Infer.
    /// </summary>
    public async Task<bool> SaveStateAsync(CancellationToken ct = default)
    {
        if (_retired)
            return false;

        await _requestGate.WaitAsync(ct);
        try
        {
            _ops.EnqueueMutation(PendingMutationType.Save);
            LastActivity = DateTime.UtcNow;
            _logger.Info("BatchSession", $"[{Key}] SaveState buffered");
            return true;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    /// <summary>
    /// Rewind — enqueues a Rewind op. Coordinator applies it (inside the gate) before the next Infer().
    /// </summary>
    public async Task<bool> RewindAsync(CancellationToken ct = default)
    {
        if (_retired || _savedState == null)
            return false;

        await _requestGate.WaitAsync(ct);
        try
        {
            _ops.EnqueueMutation(PendingMutationType.Rewind);
            LastActivity = DateTime.UtcNow;
            _logger.Info("BatchSession", $"[{Key}] Rewind buffered");
            return true;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    /// <summary>
    /// Reset — enqueues a Reset op. Coordinator disposes + recreates the conversation
    /// inside the gate; KV regions become reusable at that point.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        if (_retired)
            throw new ObjectDisposedException(nameof(BatchSession));

        await _requestGate.WaitAsync(ct);
        try
        {
            _ops.EnqueueMutation(PendingMutationType.Reset);
            LastActivity = DateTime.UtcNow;
            _logger.Info("BatchSession", $"[{Key}] Reset buffered");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    /// <summary>
    /// Enqueue a Reset AND run one cycle so the conversation is actually recreated before
    /// returning — used by the overflow safety net so recovery is deterministic.
    /// </summary>
    internal async Task ResetAndFlushAsync(CancellationToken ct = default)
    {
        await ResetAsync(ct);
        await _coordinator.RunInferCycleAsync(ct);
    }

    /// <summary>
    /// v15 KV-hygiene — feed text into cache as PROMPT with bounded sampling.
    /// Enqueues the prompt; coordinator flushes + Infers.
    /// </summary>
    public async Task<(bool success, int sampledTokens, int approxTokens)> EvaluateAsync(
        string text, int maxTokens = 1, CancellationToken ct = default)
    {
        if (_retired)
            throw new ObjectDisposedException(nameof(BatchSession));
        if (string.IsNullOrEmpty(text))
            return (true, 0, _approxTokenCount);

        await _requestGate.WaitAsync(ct);
        try
        {
            try
            {
                var effectiveText = ResolveMtmdMarker(text);
                _approxTokenCount += EstimateTokenCount(text);
                LastActivity = DateTime.UtcNow;

                _ops.EnqueuePrompt(effectiveText);

                var result = await _coordinator.RunInferCycleAsync(ct);
                if (result != DecodeResult.Ok)
                    return (false, 0, _approxTokenCount);

                var applyError = TakeLastApplyError();
                if (applyError != null)
                    return (false, 0, _approxTokenCount);

                // Retired while queued: our buffered prompt was never applied.
                if (_retired || _conversationDisposed)
                    return (false, 0, _approxTokenCount);

                // Sample and discard up to maxTokens tokens.
                // Each additional sampled token requires its own decode —
                // sampling N tokens from ONE Infer re-uses stale logits (garbage output).
                var pipe = CreateDefaultPipeline();
                var sampled = 0;
                for (var i = 0; i < maxTokens; i++)
                {
                    try
                    {
                        var tokenId = _conversation.Sample(pipe);
                        sampled++;
                        // Feed the sampled token back and decode before sampling again
                        if (i < maxTokens - 1)
                        {
#pragma warning disable CS0618
                            _ops.EnqueuePrompt(_coordinator.Context.DeTokenize(new[] { tokenId }));
#pragma warning restore CS0618
                            var cycleResult = await _coordinator.RunInferCycleAsync(ct);
                            if (cycleResult != DecodeResult.Ok)
                                break;
                        }
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
        finally
        {
            _requestGate.Release();
        }
    }

    /// <summary>True when a request is currently being served on this session (request gate held).
    /// Used by the overflow safety net: an actively-streaming session must never be reset
    /// out from under its request — it owns its own overflow handling.
    /// Snapshot value; sessions that start right after are still safe (reset is buffered
    /// behind the gate, so it applies only after the request finishes).</summary>
    internal bool IsBusy => _requestGate.CurrentCount == 0;

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

    /// <summary>
    /// Retire this session. NEVER disposes the conversation on the calling thread — the
    /// coordinator disposes retired conversations inside the cycle gate, so a disconnect
    /// racing an in-flight Infer cannot crash the native side. The coordinator disposes
    /// the executor at shutdown, which frees all remaining conversations regardless.
    /// </summary>
    public void Dispose()
    {
        if (_retired) return;
        _retired = true;
        _coordinator.Retire(this);
    }
}