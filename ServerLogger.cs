using System.Text.Json;

namespace ECAssistant.LLM;

/// <summary>
/// Simple file + console logger for the LLM server.
/// </summary>
public sealed class ServerLogger : ILogger
{
    private readonly LogLevel _minLevel;
    private readonly string? _logFile;
    private readonly object _lock = new();
    private bool _consoleEnabled = true;

    public ServerLogger(LogLevel minLevel = LogLevel.Info, string? logFile = null)
    {
        _minLevel = minLevel;
        _logFile = logFile;
    }

    public void DisableConsole() => _consoleEnabled = false;

    public void Info(string tag, string message) => Write(LogLevel.Info, tag, message);
    public void Warn(string tag, string message) => Write(LogLevel.Warn, tag, message);
    public void Error(string tag, string message) => Write(LogLevel.Error, tag, message);
    public void Debug(string tag, string message) => Write(LogLevel.Debug, tag, message);

    private void Write(LogLevel level, string tag, string message)
    {
        if (level < _minLevel) return;

        var ts = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{ts}] [{level}] [{tag}] {message}";

        lock (_lock)
        {
            if (_consoleEnabled)
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    LogLevel.Error => ConsoleColor.Red,
                    LogLevel.Warn => ConsoleColor.Yellow,
                    LogLevel.Debug => ConsoleColor.DarkGray,
                    _ => ConsoleColor.Gray
                };
                Console.WriteLine(line);
                Console.ForegroundColor = prev;
            }

            if (_logFile != null)
            {
                // Per-line File.AppendAllText (open/write/close per line) is BY DESIGN:
                // the log survives a hard crash/OOM-kill because no buffered writer
                // handle is ever left open. Cost is acceptable at server log volumes.
                try { File.AppendAllText(_logFile, line + Environment.NewLine); }
                catch { /* file logging is best-effort */ }
            }
        }
    }
}

/// <summary>
/// Logger interface used throughout ECAssistantLLM.
/// </summary>
public interface ILogger
{
    void Info(string tag, string message);
    void Warn(string tag, string message);
    void Error(string tag, string message);
    void Debug(string tag, string message);
}

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}