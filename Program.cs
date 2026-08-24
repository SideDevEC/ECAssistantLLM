using ECAssistant.LLM;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Server;

// ── Find config file ──
var configPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "llm-server.json");

if (!File.Exists(configPath))
{
    // Try current directory
    configPath = Path.Combine(Directory.GetCurrentDirectory(), "llm-server.json");
}

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"ERROR: llm-server.json not found.");
    Console.Error.WriteLine($"Searched: {AppContext.BaseDirectory}, {Directory.GetCurrentDirectory()}");
    Console.Error.WriteLine($"Usage: ECAssistant.LLM [path-to-llm-server.json]");
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
logger.Info("Main", $"Server: {config.Server.Host}:{config.Server.Port}");
logger.Info("Main", $"Models configured: {config.Models.Count}");

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
var clientManager = new ClientManager(sessionRegistry, config, logger);

var server = new LlmHttpServer(config, modelHost, sessionRegistry, scheduler, vramBudget, clientManager, logger);

// ── Handle shutdown ──
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.Info("Main", "Shutdown signal received...");
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