using System.Text;
using ECAssistantInference.Abstractions;
using ECAssistantInference.Exceptions;
using ECAssistantInference.Models;
using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Per-session inference state: own executor + KV cache.
/// Each ECAssistantCore session maps to one SessionContext on the server.
/// </summary>
public sealed class SessionContext : IDisposable
{
    private readonly ILogger _logger;
    private bool _disposed;

    public string Key { get; }
    public string ClientId { get; }
    public string SessionId { get; }
    public string ModelId { get; }

    private readonly IInferenceModel _model;
    private readonly ECAssistantInference.Models.ModelConfig _modelConfig;
    private readonly ECAssistantInference.Models.ContextConfig _ctxConfig;
    private readonly SamplingConfig _defaultSampling;
    private readonly IVisionEncoder? _vision;

    private IInferenceContext? _context;
    private IStandardExecutor? _executor;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private IInferenceState? _savedState;

    public bool IsPrefilled { get; private set; }
    public bool HasSavedState => _savedState != null;
    public int ApproxTokenCount { get; private set; }
    public uint ContextSize { get; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string? ToolsHash { get; set; }
    public double EstimatedVramMb { get; }

    public SessionContext(
        string clientId,
        string sessionId,
        string modelId,
        IInferenceModel model,
        ECAssistantInference.Models.ModelConfig modelConfig,
        ECAssistantInference.Models.ContextConfig ctxConfig,
        SamplingConfig defaultSampling,
        ILogger logger,
        IVisionEncoder? vision = null)
    {
        ClientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        Key = $"{clientId}:{sessionId}";
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ContextSize = ctxConfig.ContextSize;
        _model = model;
        _modelConfig = modelConfig;
        _ctxConfig = ctxConfig;
        _defaultSampling = defaultSampling;
        _vision = vision;

        _context = _model.CreateContext(_ctxConfig);
        _executor = _context.CreateExecutor();

        EstimatedVramMb = EstimateVramMb();
    }

    public async Task<(bool success, int tokens, long elapsedMs)> PrefillAsync(string text, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SessionContext));
        if (IsPrefilled)
            return (true, ApproxTokenCount, 0);
        if (_executor == null)
            return (false, 0, 0);

        var startMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
        var sb = new StringBuilder();

        await _ioLock.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));

            try
            {
                _executor.Prompt(text);
                var inferResult = _executor.Infer();
                if (inferResult != InferResult.Ok)
                {
                    _logger.Warn("SessionContext", $"[{Key}] Prefill infer failed: {inferResult}");
                    return (false, 0, 0);
                }

                // Sample one token to consume the prompt's logits (then discard it)
                var token = _executor.Sample(new SamplingConfig { Temperature = 0.1f, TopK = 1, MaxTokens = 1 });
                sb.Append(_context.TokenToPiece(token));
            }
            catch (OperationCanceledException)
            {
                _logger.Warn("SessionContext", $"[{Key}] Prefill timed out after 120s");
                return (false, 0, 120000);
            }
            catch (Exception ex)
            {
                _logger.Error("SessionContext", $"[{Key}] Prefill failed: {ex.Message}");
                return (false, 0, 0);
            }

            var elapsedMs = (long)((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - startMs);
            IsPrefilled = true;
            ApproxTokenCount = EstimateTokenCount(text);

            _logger.Info("SessionContext", $"[{Key}] Prefilled {ApproxTokenCount} tokens in {elapsedMs}ms");
            return (true, ApproxTokenCount, elapsedMs);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public bool SaveState()
    {
        if (_executor == null) return false;

        if (!_ioLock.Wait(ResetLockTimeout))
        {
            _logger.Warn("SessionContext", $"[{Key}] SaveState timed out waiting for the IO lock");
            return false;
        }

        try
        {
            _savedState?.Dispose();
            _savedState = _executor.SaveState();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("SessionContext", $"[{Key}] SaveState failed: {ex.Message}");
            return false;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<bool> RewindAsync()
    {
        if (_executor == null) return false;

        if (_savedState == null)
        {
            _logger.Warn("SessionContext", $"[{Key}] Rewind: no saved state");
            return false;
        }

        if (!await _ioLock.WaitAsync(ResetLockTimeout))
        {
            _logger.Warn("SessionContext", $"[{Key}] Rewind timed out waiting for the IO lock");
            return false;
        }
        try
        {
            _executor.RestoreState(_savedState);
            _logger.Info("SessionContext", $"[{Key}] Rewound to saved state");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("SessionContext", $"[{Key}] Rewind failed: {ex.Message}");
            return false;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static readonly TimeSpan ResetLockTimeout = TimeSpan.FromSeconds(30);

    public void Reset()
    {
        if (!_ioLock.Wait(ResetLockTimeout))
            throw new TimeoutException($"[{Key}] Reset timed out after {ResetLockTimeout.TotalSeconds}s waiting for the IO lock");
        try
        {
            _executor?.Dispose();
            _context?.Dispose();
            _savedState?.Dispose();
            _savedState = null;

            _context = _model.CreateContext(_ctxConfig);
            _executor = _context.CreateExecutor();

            IsPrefilled = false;
            ApproxTokenCount = 0;
            _logger.Info("SessionContext", $"[{Key}] KV cache reset");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async IAsyncEnumerable<string> InferAsync(
        string prompt,
        SamplingConfig? samplingConfig = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        IReadOnlyList<byte[]>? images = null,
        string? grammarStr = null,
        string? grammarRoot = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SessionContext));
        if (_executor == null)
            throw new InvalidOperationException("Session executor is null (reset not called?)");
        if (images is { Count: > 0 } && _vision == null)
            throw new InvalidOperationException("Model has no mmproj loaded — images are not supported on this session.");

        LastActivity = DateTime.UtcNow;

        var sb = new StringBuilder();
        IGrammar? grammar = null;
        await _ioLock.WaitAsync(ct);
        try
        {
            // Vision: prompt with images
            var effectivePrompt = prompt;
            if (_vision != null && images is { Count: > 0 })
            {
                var marker = MtmdMarkerResolver.GetMarker(_vision);
                effectivePrompt = prompt.Replace(
                    ECAssistant.LLM.Models.ChatMessageContentConverter.DefaultImageMarker,
                    marker);
                // Note: with ECAssistantInference, images are passed directly to PromptWithImages
            }

            ApproxTokenCount += EstimateTokenCount(effectivePrompt);

            var sampling = samplingConfig ?? _defaultSampling;
            if (!string.IsNullOrEmpty(grammarStr) && !string.IsNullOrEmpty(grammarRoot) && _model != null)
            {
                try { grammar = _model.CreateGrammar(grammarStr, grammarRoot); }
                catch (Exception ex)
                {
                    _logger.Warn("SessionContext", $"[{Key}] Grammar creation failed: {ex.Message}");
                    throw; // fail loud — unconstrained generation silently corrupts structured output
                }
            }

            if (_vision != null && images is { Count: > 0 })
            {
                _executor.PromptWithImages(effectivePrompt, _model, images.ToArray());
            }
            else
            {
                _executor.Prompt(effectivePrompt);
            }

            var inferResult = _executor.Infer();
            if (inferResult != InferResult.Ok)
            {
                _logger.Warn("SessionContext", $"[{Key}] Infer returned {inferResult}");
                yield break;
            }

            // Sample tokens
            var maxTokens = sampling.MaxTokens;
            var headroom = (int)ContextSize - ApproxTokenCount - 16;
            if (maxTokens > headroom)
                maxTokens = Math.Max(1, headroom);

            for (int i = 0; i < maxTokens; i++)
            {
                int token;
                try
                {
                    token = grammar != null
                        ? _executor.SampleWithGrammar(sampling, grammar)
                        : _executor.Sample(sampling);
                }
                catch (Exception ex)
                {
                    _logger.Warn("SessionContext", $"[{Key}] Sample failed: {ex.Message}");
                    break;
                }

                if (_context.IsEos(token))
                    break;

                var piece = _context.TokenToPiece(token);
                sb.Append(piece);
                yield return piece;

                // Feed sampled token back for next decode
                _executor.PromptTokens(new[] { token });
                var result = _executor.Infer();
                if (result != InferResult.Ok)
                    break;
            }

            ApproxTokenCount += EstimateTokenCount(sb.ToString());
        }
        finally
        {
            grammar?.Dispose();
            _ioLock.Release();
        }
    }

    public IInferenceContext? GetContext() => _context;

    public async Task<(bool success, int sampledTokens, int approxTokens)> EvaluateAsync(
        string text, int maxTokens = 1, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SessionContext));
        if (_executor == null)
            return (false, 0, ApproxTokenCount);
        if (string.IsNullOrEmpty(text))
            return (true, 0, ApproxTokenCount);

        await _ioLock.WaitAsync(ct);
        try
        {
            var boundedSampling = new SamplingConfig
            {
                Temperature = 0.1f,
                TopK = 1,
                MaxTokens = Math.Clamp(maxTokens, 1, MaxInferenceTokens),
            };

            ApproxTokenCount += EstimateTokenCount(text);
            _executor.Prompt(text);
            _executor.Infer();

            var sampled = 0;
            for (int i = 0; i < maxTokens; i++)
            {
                try
                {
                    var token = _executor.Sample(boundedSampling);
                    sampled++;
                    if (i < maxTokens - 1)
                    {
                        _executor.PromptTokens(new[] { token });
                        _executor.Infer();
                    }
                }
                catch { break; }
            }

            ApproxTokenCount += sampled;
            return (true, sampled, ApproxTokenCount);
        }
        catch (Exception)
        {
            return (false, 0, ApproxTokenCount);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private const int MaxInferenceTokens = 32768;

    private static int EstimateTokenCount(string text) => text.Length / 4;

    private double EstimateVramMb()
    {
        var approxLayers = 28;
        var approxDim = 4096;
        return Math.Round(2.0 * approxLayers * ContextSize * approxDim * 2 / (1024 * 1024), 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _executor?.Dispose();
        _context?.Dispose();
        _savedState?.Dispose();
        _savedState = null;
    }
}
