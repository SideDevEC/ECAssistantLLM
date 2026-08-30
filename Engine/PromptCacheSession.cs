using LLama;
using LLama.Common;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Persistent prompt-cache session reusing warm KV state across stateless/background calls
/// (summarize, plan, decompose, intent) using ONLY supported LLamaSharp primitives.
///
/// Why this design (empirically verified against llama.cpp 0.x via LLamaSharp 0.27):
/// - Manual kv_cache_seq_rm underneath an InteractiveExecutor desyncs the executor's internal
///   position bookkeeping → 'InvalidInputBatch' or silent zero-token generations.
/// - An InteractiveExecutor also cannot be reused for consecutive independent prompts: it
///   carries completion state from the previous anti-prompt stop and yields nothing.
/// - SaveState/LoadState snapshots across FRESH executors are supported, cheap (<10ms restore),
///   and are already proven by the main session's rewind feature.
///
/// Reuse paths, in priority order:
/// 1. Growth:    new prompt extends the entire last prompt → restore last snapshot, decode
///               only the suffix (append-only conversations).
/// 2. Template:  stable header learned from recent prompts, checkpointed alone in its own
///               context → requests sharing it restore that snapshot and decode only their
///               variable payload.
/// 3. Cold:      otherwise a full prefill, identical cost to pre-cache behavior.
/// </summary>
public sealed class PromptCacheSession : IDisposable
{
    /// <summary>Minimum template length (chars) before template checkpoints are kept.</summary>
    public const int MinCheckpointChars = 512;

    /// <summary>Back off the learned split from the raw common prefix so tokenizer edge effects cannot corrupt round-tripping.</summary>
    public const int SplitSafetyMarginChars = 96;

    private const int RecentPromptsTracked = 5;

    private readonly LLamaWeights _weights;
    private readonly ModelParams _modelParams;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private readonly Queue<string> _recentPrompts = new();

    private LLamaContext? _context;

    // Checkpoint A: end of the stable template region (lives in its own retired context).
    private StatefulExecutorBase.ExecutorBaseState? _templateState;
    private string _templateText = string.Empty;

    // Checkpoint B: end of the most recent complete prompt (its context is retired too).
    private StatefulExecutorBase.ExecutorBaseState? _lastState;
    private string _lastPromptText = string.Empty;

    /// <summary>Consecutive requests served from the CURRENT template snapshot.</summary>
    private int _templateReuseCount;

    /// <summary>
    /// Max consecutive reuses of one template snapshot before it is force-refreshed.
    /// Guards against low-level KV drift accumulating across restores (observed as
    /// gradually degraded output after many consecutive warm loads on the big model).
    /// Refresh costs one cold prefill — amortized strongly against the wins in between.
    /// </summary>
    public const int MaxConsecutiveReuses = 3;

    private bool _disposed;

    public PromptCacheSession(string modelId, LLamaWeights weights, ModelParams modelParams, ILogger logger)
    {
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        _weights = weights ?? throw new ArgumentNullException(nameof(weights));
        _modelParams = modelParams ?? throw new ArgumentNullException(nameof(modelParams));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ModelId { get; }

    /// <summary>Requests served from a warm snapshot.</summary>
    public int CacheHits { get; private set; }

    /// <summary>Requests that required a full cold prefill.</summary>
    public int ColdStarts { get; private set; }

    /// <summary>Chars covered by the current template checkpoint.</summary>
    public int TemplateChars => _templateText.Length;

    /// <summary>Infer with streaming, resuming from the best matching warm snapshot.</summary>
    public async IAsyncEnumerable<string> InferAsync(
        string prompt,
        InferenceParams inferenceParams,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Fresh executor per request — mandatory: reused executors carry completion state
        // from prior generations and silently yield nothing.
        var oldContext = _context;
        var context = _weights.CreateContext(_modelParams);
        var executor = new InteractiveExecutor(
            context,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LLamaContext>.Instance);
        oldContext?.Dispose();
        _context = context;

        await _ioLock.WaitAsync(ct);
        try
        {
            bool resumed = false;
            string effective = prompt;

            if (!string.IsNullOrEmpty(_lastPromptText) &&
                _lastState != null &&
                prompt.StartsWith(_lastPromptText, StringComparison.Ordinal))
            {
                await executor.LoadState(_lastState);
                effective = prompt[_lastPromptText.Length..];
                resumed = true;
                CacheHits++;
                _logger.Info("PromptCache", $"[{ModelId}] Growth reuse ({_lastPromptText.Length} chars cached) — decoding suffix ({effective.Length} chars)");
            }
            else if (_templateState != null &&
                     _templateText.Length >= MinCheckpointChars &&
                     _templateReuseCount < MaxConsecutiveReuses &&
                     prompt.StartsWith(_templateText, StringComparison.Ordinal))
            {
                await executor.LoadState(_templateState);
                effective = prompt[_templateText.Length..];
                resumed = true;
                CacheHits++;
                _templateReuseCount++;
                _logger.Info("PromptCache", $"[{ModelId}] Template reuse #{_templateReuseCount} ({_templateText.Length}-char header cached) — decoding payload ({effective.Length} chars)");
            }

            if (!resumed)
            {
                ColdStarts++;
                if (_templateReuseCount >= MaxConsecutiveReuses)
                {
                    // Force-refresh the template snapshot from a clean context.
                    _templateState = null;
                    _logger.Info("PromptCache", $"[{ModelId}] Warm-reuse cap reached — template will be re-checkpointed");
                }
                _templateReuseCount = 0;
            }

            var outputSb = new System.Text.StringBuilder();
            await foreach (var token in executor.InferAsync(effective, inferenceParams, ct))
            {
                outputSb.Append(token);
                yield return token;
            }

            // Snapshot AFTER successful generation; context must stay alive until snapshot taken.
            _lastPromptText = prompt;
            _lastState = executor.GetStateData();

            TrackRecentPrompt(prompt);
        }
        finally
        {
            _ioLock.Release();
        }

        await LearnTemplateCheckpointAsync();
    }

    /// <summary>Drop all cached state; next call performs a full cold prefill.</summary>
    private void TrackRecentPrompt(string prompt)
    {
        _recentPrompts.Enqueue(prompt);
        while (_recentPrompts.Count > RecentPromptsTracked)
            _recentPrompts.Dequeue();
    }

    /// <summary>
    /// Learn/refine the stable template: longest common char prefix over recent prompts,
    /// snapped back to the nearest newline inside the safety margin. When it changes
    /// meaningfully, replay JUST the template through a disposable context and snapshot it.
    /// Runs outside the io-lock — uses its own context, never touches the active stream.
    /// </summary>
    private async Task LearnTemplateCheckpointAsync()
    {
        try
        {
            if (_recentPrompts.Count < 2)
                return;

            var prompts = _recentPrompts.ToArray();
            var rawCommon = prompts[0].Length;
            for (var i = 1; i < prompts.Length; i++)
            {
                var p = prompts[i];
                var limit = Math.Min(rawCommon, p.Length);
                int j = 0;
                while (j < limit && prompts[0][j] == p[j]) j++;
                rawCommon = j;
            }

            var ceiling = rawCommon - SplitSafetyMarginChars;
            if (ceiling < MinCheckpointChars)
                return;

            var candidate = prompts[0];
            var split = -1;
            for (var i = Math.Min(ceiling, candidate.Length - 1); i >= MinCheckpointChars; i--)
            {
                if (candidate[i] == '\n') { split = i + 1; break; }
            }
            // Fallback: no line boundary in the window (e.g. long uniform fillers).
            // The safety margin back-off already guards against BPE merges spanning the cut.
            if (split < MinCheckpointChars && ceiling >= MinCheckpointChars)
                split = ceiling;
            if (split < MinCheckpointChars)
            {
                _logger.Debug("PromptCache", $"[{ModelId}] Template learning: common prefix too short ({rawCommon} chars)");
                return;
            }

            var newTemplate = candidate[..split];

            var grew = _templateState == null || newTemplate.Length > _templateText.Length;
            var shrunkALot = _templateState != null && newTemplate.Length < _templateText.Length * 3 / 4;
            if (!grew && !shrunkALot)
                return;

            using var ctx = _weights.CreateContext(_modelParams);
            var ex = new InteractiveExecutor(
                ctx,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LLamaContext>.Instance);

            // Prefill-only pass: MaxTokens 1 stops immediately after consuming the template.
            await foreach (var _ in ex.InferAsync(newTemplate, new InferenceParams { MaxTokens = 1 }, CancellationToken.None))
            {
                break;
            }

            _templateText = newTemplate;
            _templateState = ex.GetStateData();
            _templateReuseCount = 0;

            // Last-prompt snapshot includes older cells inconsistent with the new split — drop it.
            _lastState = null;
            _lastPromptText = string.Empty;

            _logger.Info("PromptCache", $"[{ModelId}] Template checkpoint saved ({_templateText.Length} chars)");
        }
        catch (Exception ex)
        {
            _logger.Warn("PromptCache", $"[{ModelId}] Template learning skipped: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context?.Dispose();
        _context = null;
    }
}
