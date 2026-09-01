using ECAssistant.LLM;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Server;

// ── Parse args: [--root <dir>] [--port <N>] [path-to-llm-server.json] ──
string? rootDir = null;
int? portOverride = null;
string? configPath = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--root" && i + 1 < args.Length)
        rootDir = args[++i];
    else if (args[i] == "--port" && i + 1 < args.Length)
    {
        // Parse into a local WITHOUT consuming the next arg on failure — a
        // non-numeric value after --port may itself be a positional config path.
        if (int.TryParse(args[i + 1], out var p))
        {
            portOverride = p;
            i++;
        }
    }
    else if (!args[i].StartsWith("--"))
        configPath = args[i];
}

// ── Resolve root directory ──
// The root dir is passed by the calling application (e.g. ECAssistantCore).
// Everything (config, logs, models) lives under this directory.
if (string.IsNullOrEmpty(rootDir))
{
    // Fallback: use current directory if no root specified (standalone execution)
    rootDir = Directory.GetCurrentDirectory();
}

Directory.CreateDirectory(rootDir);

// ── Resolve config path ──
var configExplicit = configPath != null;
if (string.IsNullOrEmpty(configPath))
    configPath = Path.Combine(rootDir, "llm-server.json");

// ── Generate default config only for STANDALONE runs (no explicit config path) ──
// When launched with an explicit path (e.g. by ECAssistantCore), the caller owns the
// config — a missing file is an error, never a reason to invent defaults.
if (!File.Exists(configPath))
{
    if (configExplicit)
    {
        Console.Error.WriteLine($"ERROR: Config file passed on the command line does not exist: {configPath}");
        Console.Error.WriteLine($"The calling application (ECAssistantCore) must write llm-server.json before launch.");
        return 1;
    }
    var defaultConfig = LlmServerConfig.GenerateDefault();
    LlmServerConfig.Save(defaultConfig, configPath);
    Console.WriteLine($"[ECAssistantLLM] Generated default config: {configPath}");
    Console.WriteLine($"[ECAssistantLLM] Edit it to point to your model files (models/*.gguf).");
}

// ── Load config ──
var (config, loadError) = LlmServerConfig.TryLoad(configPath);
if (config == null)
{
    Console.Error.WriteLine($"ERROR: Failed to load config: {loadError}");
    return 1;
}

// ── Setup logger ──
// Invariant culture: log-level config values must not be reshaped by the OS locale.
var logLevel = config.Logging.Level.ToLowerInvariant() switch
{
    "debug" => LogLevel.Debug,
    "warn" => LogLevel.Warn,
    "error" => LogLevel.Error,
    _ => LogLevel.Info
};

// Resolve log file path relative to root directory
var logFilePath = Path.IsPathRooted(config.Logging.File)
    ? config.Logging.File
    : Path.Combine(rootDir, config.Logging.File);
var logger = new ServerLogger(logLevel, logFilePath);

logger.Info("Main", $"ECAssistantLLM v{LlmServerInfo.Version}");
logger.Info("Main", $"Root: {rootDir}");
logger.Info("Main", $"Config: {configPath}");

// ── Apply port override from command line ──
if (portOverride.HasValue)
{
    config.Server.Port = portOverride.Value;
    logger.Info("Main", $"Port override from CLI: {portOverride.Value}");
}

logger.Info("Main", $"Server: {config.Server.Host}:{config.Server.Port}");
logger.Info("Main", $"Models configured: {config.Models.Count}");
logger.Info("Main", $"Shutdown on last client: {config.Server.ShutdownOnLastClient}");

// ── Resolve model paths relative to root directory ──
// ROOT-ONLY contract (v12.11): relative paths resolve ONLY as {rootDir}/{path} or
// {rootDir}/models/{filename}. No exe-dir / CWD fallback — paths outside the root
// are rejected at load time (ModelSlot).
foreach (var model in config.Models)
{
    if (!Path.IsPathRooted(model.Path))
    {
        var fileName = Path.GetFileName(model.Path);
        var candidates = new[]
        {
            Path.Combine(rootDir, model.Path),
            Path.Combine(rootDir, "models", fileName)
        };
        model.Path = candidates.FirstOrDefault(File.Exists) ?? model.Path;
        // No dev-environment fallbacks beyond the candidates above.
    }
    logger.Info("Main", $"  Model '{model.Id}': {model.Path}");
}

// ── Initialize components ──
var modelHost = new MultiModelHost(config, logger, rootDir);
var scheduler = new InferenceScheduler(logger);
var vramBudget = new VramBudget(config);

logger.Info("Main", "Loading models...");
try
{
    await modelHost.LoadAllAsync();
}
catch (Exception ex)
{
    logger.Error("Main", $"Failed to load models: {ex.Message}");
    // v12.8: keep the server alive even when models fail to load — the app can still
    // reach /reinstall and the health endpoint instead of the child dying silently.
    logger.Warn("Main", "Server continuing WITHOUT loaded models. Fix llm-server.json paths or run the installer.");
}

var sessionRegistry = new SessionRegistry(modelHost, scheduler, config, logger, vramBudget);

// ── Shutdown coordination ──
var cts = new CancellationTokenSource();

// ClientManager triggers this when the last client disconnects
void OnLastClientDisconnected()
{
    logger.Info("Main", "Last client left — shutting down server...");
    cts.Cancel();
}

var clientManager = new ClientManager(sessionRegistry, config, logger, OnLastClientDisconnected);

var server = new LlmHttpServer(config, modelHost, sessionRegistry, scheduler, vramBudget, clientManager, logger, cts);

// ── Handle external shutdown signals ──
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.Info("Main", "Shutdown signal received (Ctrl+C)...");
    try { cts.Cancel(); } catch (ObjectDisposedException) { /* server already disposed during shutdown */ }
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    logger.Info("Main", "Process exiting...");
    try { cts.Cancel(); } catch (ObjectDisposedException) { /* server already disposed during shutdown */ }
};

// ── Run ──
logger.Info("Main", "Starting server...");
try
{
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // Normal shutdown
    logger.Info("Main", "Shutdown complete.");
}
catch (Exception ex)
{
    logger.Error("Main", $"Server error: {ex.Message}");
    return 1;
}
finally
{
    server.Dispose();
    logger.Info("Main", "Server stopped.");
}

return 0;