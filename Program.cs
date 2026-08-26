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
    else if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var p))
        portOverride = p;
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
if (string.IsNullOrEmpty(configPath))
    configPath = Path.Combine(rootDir, "llm-server.json");

// ── Generate default config if missing ──
if (!File.Exists(configPath))
{
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
var logLevel = config.Logging.Level.ToLower() switch
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

logger.Info("Main", $"ECAssistantLLM v1.0.0");
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
// The LLM server resolves model paths from {rootDir}/models/ or absolute paths
foreach (var model in config.Models)
{
    if (!Path.IsPathRooted(model.Path))
    {
        var resolved = Path.Combine(rootDir, model.Path);
        if (File.Exists(resolved))
        {
            model.Path = resolved;
        }
        else
        {
            // Also try {rootDir}/models/{filename}
            var inModelsDir = Path.Combine(rootDir, "models", Path.GetFileName(model.Path));
            if (File.Exists(inModelsDir))
                model.Path = inModelsDir;
        }
    }
    logger.Info("Main", $"  Model '{model.Id}': {model.Path}");
}

// ── Initialize components ──
var modelHost = new MultiModelHost(config, logger);
var scheduler = new InferenceScheduler(logger);
var vramBudget = new VramBudget(config);

logger.Info("Main", "Loading models...");
try
{
    modelHost.LoadAll();
}
catch (Exception ex)
{
    logger.Error("Main", $"Failed to load models: {ex.Message}");
    Console.Error.WriteLine($"FATAL: Failed to load models: {ex.Message}");
    return 1;
}

var sessionRegistry = new SessionRegistry(modelHost, scheduler, config, logger);

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
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    logger.Info("Main", "Process exiting...");
    cts.Cancel();
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