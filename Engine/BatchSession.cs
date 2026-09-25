using System.Text;
using ECAssistantInference.Abstractions;
using ECAssistantInference.Models;

namespace ECAssistant.LLM.Engine;

public sealed class BatchSession : IDisposable
{
    private readonly BatchInferenceCoordinator _coordinator;
    private readonly IVisionEncoder? _vision;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    private IConversation _conversation;
    private readonly BatchOpBuffer _ops = new();

    private IConversationState? _savedState;
    private int _savedAtTokenCount;

    private int _exactTokenCount;
    private bool _isPrefilled;
    private Exception? _lastApplyError;

    private volatile bool _retired;
    private bool _conversationDisposed;

    public string Key { get; }
    public string ClientId { get; }
    public string SessionId { get; }
    public string ModelId { get; }
    public uint ContextSize => _coordinator.Context.ContextSize;
    public int ApproxTokenCount => _exactTokenCount;
    public bool IsPrefilled => _isPrefilled;
    public bool HasSavedState => _savedState != null;

    public double EstimatedVramMb => Math.Round(
        2.0 * 28 * Math.Max(_exactTokenCount, 1) * 4096 * 2 / (1024.0 * 1024.0), 1);

    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string? ToolsHash { get; set; }

    internal bool IsBusy => _requestGate.CurrentCount == 0;
    internal BatchInferenceCoordinator Coordinator => _coordinator;

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
        _vision = coordinator.Vision;
        _conversation = coordinator.LeaseConversation()
            ?? throw new InvalidOperationException(
                $"Conversation pool exhausted for model '{modelId}' — all {_coordinator.PoolSize} slots in use. " +
                "Increase max_sessions or reduce concurrent batch sessions.");
        _coordinator.Register(this);
    }

    internal bool TryDequeueOp(out PendingMutation op) => _ops.TryDequeue(out op!);

    internal void DisposeConversationNow()
    {
        if (_conversationDisposed) return;
        _conversationDisposed = true;
        _conversation.Dispose();
        _savedState?.Dispose();
        _savedState = null;
    }

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
                    _savedState = _conversation.SaveState();
                    _savedAtTokenCount = _exactTokenCount;
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

    private Exception? TakeLastApplyError()
    {
        var e = _lastApplyError;
        _lastApplyError = null;
        return e;
    }

    private void ApplyPromptCore(string prompt, IReadOnlyList<byte[]>? images)
    {
        if (_vision != null && images is { Count: > 0 })
        {
            _conversation.PromptWithImages(prompt, _vision, images.Select(b => b).ToArray());
        }
        else
        {
            _conversation.Prompt(prompt);
        }
    }

    private void ApplyRewindCore()
    {
        if (_savedState == null) return;
        var tokensToRewind = _exactTokenCount - _savedAtTokenCount;
        if (tokensToRewind > 0)
        {
            try { _conversation.Rewind(tokensToRewind); }
            catch { }
        }
        _exactTokenCount = _savedAtTokenCount;
        _savedState?.Dispose();
        _savedState = null;
    }

    private void ApplyResetCore()
    {
        if (_exactTokenCount > 0)
        {
            try { _conversation.Rewind(_exactTokenCount); }
            catch { }
        }
        _savedState?.Dispose();
        _savedState = null;
        _isPrefilled = false;
        _exactTokenCount = 0;
    }

    public async Task<(bool success, int tokens, long elapsedMs)> PrefillAsync(string text, CancellationToken ct = default)
    {
        if (_retired)
            throw new ObjectDisposedException(nameof(BatchSession));
        if (_isPrefilled)
            return (true, _exactTokenCount, 0);

        await _requestGate.WaitAsync(ct);
        try
        {
            var startMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            try
            {
                var promptText = ResolveMtmdMarker(text);
                _ops.EnqueuePrompt(promptText);

                var result = await _coordinator.RunInferCycleAsync(ct);
                if (result != InferResult.Ok)
                {
                    _logger.Error("BatchSession", $"[{Key}] Prefill Infer failed: {result}");
                    return (false, 0, 0);
                }

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
                _exactTokenCount = CountTokensExact(text);
                var elapsedMs = (long)((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - startMs);
                LastActivity = DateTime.UtcNow;
                _logger.Info("BatchSession", $"[{Key}] Prefilled ~{_exactTokenCount} tokens in {elapsedMs}ms");
                return (true, _exactTokenCount, elapsedMs);
            }
            catch (Exception ex)
            {
                _logger.Error("BatchSession", $"[{Key}] Prefill failed: {ex.Message}");
                return (false, 0, 0);
            }
        }
        finally { _requestGate.Release(); }
    }

    public async IAsyncEnumerable<string> InferAsync(
        string prompt,
        SamplingConfig? samplingConfig = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        IReadOnlyList<byte[]>? images = null,
        string? grammarStr = null,
        string? grammarRoot = null)
    {
        if (_retired)
            throw new ObjectDisposedException(nameof(BatchSession));
        if (images is { Count: > 0 } && _vision == null)
            throw new InvalidOperationException("Model has no mmproj loaded — images are not supported on this session.");

        IGrammar? grammar = null;
        await _requestGate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(grammarStr) && !string.IsNullOrEmpty(grammarRoot) && _coordinator.Model != null)
            {
                try { grammar = _coordinator.Model.CreateGrammar(grammarStr, grammarRoot); }
                catch { }
            }

            await foreach (var token in InferCoreAsync(prompt, samplingConfig, ct, images, grammar))
                yield return token;
        }
        finally
        {
            grammar?.Dispose();
            _requestGate.Release();
        }
    }

    private async IAsyncEnumerable<string> InferCoreAsync(
        string prompt,
        SamplingConfig? samplingConfig,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        IReadOnlyList<byte[]>? images,
        IGrammar? grammar)
    {
        LastActivity = DateTime.UtcNow;

        var sampling = samplingConfig ?? new SamplingConfig { Temperature = 0.3f, TopP = 0.95f, TopK = 40 };

        var effectivePrompt = prompt;
        if (_vision != null && images is { Count: > 0 })
        {
            var marker = MtmdMarkerResolver.GetMarker(_vision);
            effectivePrompt = prompt.Replace(
                ECAssistant.LLM.Models.ChatMessageContentConverter.DefaultImageMarker, marker);
            _logger.Info("BatchSession", $"[{Key}] Vision: {images.Count} media buffered");
        }

        if (!string.IsNullOrEmpty(effectivePrompt))
            _exactTokenCount += CountTokensExact(effectivePrompt);

        var maxTokens = sampling.MaxTokens;
        var headroom = (int)ContextSize - _exactTokenCount - 16;
        if (maxTokens > headroom)
            maxTokens = Math.Max(1, headroom);

        var generated = 0;
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(effectivePrompt))
            _ops.EnqueuePrompt(effectivePrompt, images);

        while (generated < maxTokens)
        {
            if (_retired || _conversationDisposed)
            {
                _logger.Warn("BatchSession", $"[{Key}] Session retired mid-generation — aborting stream");
                break;
            }

            InferResult result;
            try
            {
                result = await _coordinator.RunInferCycleAsync(ct);
            }
            catch (Exception ex) when (IsContextOverflow(ex))
            {
                throw new InvalidOperationException($"[{Key}] Context overflowed during generation: {ex.Message}");
            }

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

            if (result != InferResult.Ok)
            {
                _logger.Warn("BatchSession", $"[{Key}] Infer returned {result} during generation");
                break;
            }

            int tokenId;
            try
            {
                tokenId = grammar != null
                    ? _conversation.SampleWithGrammar(sampling, grammar)
                    : _conversation.Sample(sampling);
            }
            catch (Exception ex)
            {
                _logger.Warn("BatchSession", $"[{Key}] Sample failed: {ex.Message}");
                break;
            }

            if (_coordinator.Context.IsEos(tokenId))
                break;

            var text = _coordinator.Context.TokenToPiece(tokenId);
            sb.Append(text);
            generated++;
            yield return text;

            _ops.EnqueuePrompt(text);
        }

        _exactTokenCount += CountTokensExact(sb.ToString());
    }

    public async Task<bool> SaveStateAsync(CancellationToken ct = default)
    {
        if (_retired) return false;
        await _requestGate.WaitAsync(ct);
        try
        {
            _ops.EnqueueMutation(PendingMutationType.Save);
            LastActivity = DateTime.UtcNow;
            return true;
        }
        finally { _requestGate.Release(); }
    }

    public async Task<bool> RewindAsync(CancellationToken ct = default)
    {
        if (_retired || _savedState == null) return false;
        await _requestGate.WaitAsync(ct);
        try
        {
            _ops.EnqueueMutation(PendingMutationType.Rewind);
            LastActivity = DateTime.UtcNow;
            return true;
        }
        finally { _requestGate.Release(); }
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        if (_retired)
            throw new ObjectDisposedException(nameof(BatchSession));
        await _requestGate.WaitAsync(ct);
        try
        {
            _ops.EnqueueMutation(PendingMutationType.Reset);
            LastActivity = DateTime.UtcNow;
        }
        finally { _requestGate.Release(); }
    }

    internal async Task ResetAndFlushAsync(CancellationToken ct = default)
    {
        await ResetAsync(ct);
        await _coordinator.RunInferCycleAsync(ct);
    }

    public async Task<(bool success, int sampledTokens, int approxTokens)> EvaluateAsync(
        string text, int maxTokens = 1, CancellationToken ct = default)
    {
        if (_retired)
            throw new ObjectDisposedException(nameof(BatchSession));
        if (string.IsNullOrEmpty(text))
            return (true, 0, _exactTokenCount);

        await _requestGate.WaitAsync(ct);
        try
        {
            try
            {
                var effectiveText = ResolveMtmdMarker(text);
                _exactTokenCount += CountTokensExact(text);
                LastActivity = DateTime.UtcNow;

                _ops.EnqueuePrompt(effectiveText);

                var result = await _coordinator.RunInferCycleAsync(ct);
                if (result != InferResult.Ok)
                    return (false, 0, _exactTokenCount);

                var applyError = TakeLastApplyError();
                if (applyError != null)
                    return (false, 0, _exactTokenCount);

                if (_retired || _conversationDisposed)
                    return (false, 0, _exactTokenCount);

                var sampling = new SamplingConfig { Temperature = 0.3f, TopK = 40 };
                var sampled = 0;
                for (var i = 0; i < maxTokens; i++)
                {
                    try
                    {
                        var tokenId = _conversation.Sample(sampling);
                        sampled++;
                        if (i < maxTokens - 1)
                        {
                            _ops.EnqueuePrompt(_coordinator.Context.TokenToPiece(tokenId));
                            var cycleResult = await _coordinator.RunInferCycleAsync(ct);
                            if (cycleResult != InferResult.Ok)
                                break;
                        }
                    }
                    catch { break; }
                }

                _exactTokenCount += sampled;
                return (true, sampled, _exactTokenCount);
            }
            catch (Exception)
            {
                return (false, 0, _exactTokenCount);
            }
        }
        finally { _requestGate.Release(); }
    }

    public IInferenceContext? GetContext() => _coordinator?.Context;

    private string ResolveMtmdMarker(string text)
    {
        if (_vision == null)
            return text;
        try
        {
            var marker = MtmdMarkerResolver.GetMarker(_vision);
            return text.Replace(ECAssistant.LLM.Models.ChatMessageContentConverter.DefaultImageMarker, marker);
        }
        catch { return text; }
    }

    private static bool IsContextOverflow(Exception ex)
    {
        var msg = ex.Message ?? "";
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            msg += " " + (inner.Message ?? "");
        return msg.Contains("Context overflowed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("native memory shifting", StringComparison.OrdinalIgnoreCase);
    }

    private int CountTokensExact(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try { return _coordinator.Context.Tokenize(text, addBos: false, parseSpecial: true).Length; }
        catch { return text.Length / 4; }
    }

    public void Dispose()
    {
        if (_retired) return;
        _retired = true;
        _coordinator.Retire(this);
    }
}
