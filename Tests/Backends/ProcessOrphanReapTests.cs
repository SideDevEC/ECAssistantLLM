using System.Diagnostics;
using System.Net;
using System.Text;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Engine.Backends;
using ECAssistant.LLM.Server;
using Xunit;

namespace ECAssistant.LLM.Tests.Backends;

/// <summary>
/// Cross-platform orphan-proofing: PID files + safe reaping (binary-name guard so a
/// recycled PID is never killed) + OS-verified port probing.
/// </summary>
public sealed class ProcessOrphanReapTests : IDisposable
{
    private readonly string _dir;
    private readonly ILogger _logger = new ServerLogger(LogLevel.Error);

    private static bool IsWindows => OperatingSystem.IsWindows();

    public ProcessOrphanReapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "orphan-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static Process StartSleep()
    {
        var psi = new ProcessStartInfo("/bin/sleep", "300")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("failed to start sleep");
    }

    private string WritePidFile(int pid, string binaryName)
    {
        var path = Path.Combine(_dir, "test.pid");
        File.WriteAllText(path, $"{pid}\t{binaryName}");
        return path;
    }

    [Fact]
    public void ReapOrphan_DeadPid_DeletesFile()
    {
        if (IsWindows) return; // /bin/sleep is unix-only; cross-OS coverage on ubuntu/macos runners

        using var proc = StartSleep();
        var pid = proc.Id;
        proc.Kill();
        proc.WaitForExit();

        var file = WritePidFile(pid, "sleep");
        ProcessModelInstance.ReapOrphan(file, _logger);

        Assert.False(File.Exists(file)); // stale file cleaned up
    }

    [Fact]
    public void ReapOrphan_MissingFile_NoOp()
    {
        var file = Path.Combine(_dir, "missing.pid");
        ProcessModelInstance.ReapOrphan(file, _logger); // must not throw
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void ReapOrphan_RecycledPid_NeverKillsUnrelatedProcess()
    {
        if (IsWindows) return; // /bin/sleep is unix-only; cross-OS coverage on ubuntu/macos runners

        // Live process whose real name (sleep) does NOT match the recorded name
        // (simulates PID reuse) — the reap must leave it alive and clean the file.
        using var proc = StartSleep();
        var file = WritePidFile(proc.Id, "llama-server");

        ProcessModelInstance.ReapOrphan(file, _logger);

        Assert.False(proc.HasExited); // untouched
        Assert.False(File.Exists(file)); // stale record removed
        proc.Kill();
    }

    [Fact]
    public void ReapOrphan_MatchingBinaryName_KillsOrphan()
    {
        if (IsWindows) return; // /bin/sleep is unix-only; cross-OS coverage on ubuntu/macos runners

        // A real process whose binary name matches the record — the orphan-reap must
        // kill it (this is the llama-server-left-behind scenario).
        var fakeBinary = Path.Combine(_dir, "llama-server-fake");
        File.Copy("/bin/sleep", fakeBinary);
        using var proc = Process.Start(new ProcessStartInfo(fakeBinary, "300")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("failed to start fake binary");

        var file = WritePidFile(proc.Id, "llama-server-fake");
        ProcessModelInstance.ReapOrphan(file, _logger);

        // give the kill a moment
        proc.WaitForExit(TimeSpan.FromSeconds(10));
        Assert.True(proc.HasExited);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void IsPortFree_FalseWhenBound_TrueWhenFree()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var busyPort = ((IPEndPoint)l.LocalEndpoint).Port;
        try
        {
            Assert.False(BackendPortAllocator.IsPortFree(busyPort));
        }
        finally { l.Stop(); }

        // after stop the port is free again (brief retry — kernel may hold it briefly)
        var free = false;
        for (var i = 0; i < 20 && !free; i++)
        {
            free = BackendPortAllocator.IsPortFree(busyPort);
            if (!free) Thread.Sleep(50);
        }
        Assert.True(free);
    }

    [Fact]
    public void ReapOrphan_MalformedFile_DeletesFile()
    {
        var file = Path.Combine(_dir, "bad.pid");
        File.WriteAllText(file, "not-a-pid-file");
        ProcessModelInstance.ReapOrphan(file, _logger);
        Assert.False(File.Exists(file));
    }
}
