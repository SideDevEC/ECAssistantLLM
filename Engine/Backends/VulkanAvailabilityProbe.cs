using System.Runtime.InteropServices;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Determines whether Vulkan would be the GPU backend for in-process inference.
/// Replaces the old LLamaSharp auto-detection logic. macOS always uses Metal.
/// Stateless utility — no mutable state.
/// </summary>
public static class VulkanAvailabilityProbe
{
    public static bool IsVulkanPrimary()
    {
        if (OperatingSystem.IsMacOS())
            return false;
        return !IsCudaDriverPresent();
    }

    private static bool IsCudaDriverPresent()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return NativeLibrary.TryLoad("nvcuda", out _);
            if (OperatingSystem.IsLinux())
                return NativeLibrary.TryLoad("libcuda.so.1", out _);
            return false;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
