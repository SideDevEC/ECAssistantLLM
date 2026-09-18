using System.Runtime.InteropServices;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>Detects the current runtime platform. Stateless utility — no mutable state.</summary>
public static class PlatformDetector
{
    // Stateless utility — no mutable state

    /// <summary>The platform this process is running on.</summary>
    public static PlatformId Current()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return System.Runtime.InteropServices.Architecture.Arm64 == RuntimeInformation.ProcessArchitecture
                ? PlatformId.OsxArm64
                : throw new PlatformNotSupportedException("macOS Intel is not supported by the Process backend");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return PlatformId.WinX64;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return PlatformId.LinuxX64;
        throw new PlatformNotSupportedException($"Unsupported OS: {RuntimeInformation.OSDescription}");
    }
}
