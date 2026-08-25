using ECAssistant.LLM;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Server;

// ── Parse args: [--port <N>] [path-to-llm-server.json] ──
int? portOverride = null;
string? configPath = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var p))
        portOverride = p;
    else if (!args[i].StartsWith("--"))
        configPath = args[i];
}

// Fall back to default config path
configPath ??= Path.Combine(AppContext.BaseDirectory, "llm-server.json");

if (!File.Exists(configPath))
{
    // Try current directory
    configPath = Path.Combine(Directory.GetCurrentDirectory(), "llm-server.json");
}

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"ERROR: llm-server.json not found.");
    Console.Error.WriteLine($"Searched: {AppContext.BaseDirectory}, {Directory.GetCurrentDirectory()}");
    Console.Error.WriteLine($"Usage: ECAssistant.LLM [--port <N>] [path-to-llm-server.json]");
    return 1;
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
var logFilePath = Path.IsPathRooted(config.Logging.File)
    ? config.Logging.File
    : Path.Combine(Path.GetDirectoryName(configPath) ?? ".", config.Logging.File);
var logger = new ServerLogger(logLevel, logFilePath);

logger.Info("Main", $"ECAssistantLLM v1.0.0");
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