using System.Runtime.InteropServices;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Determines whether Vulkan is the GPU backend an in-process LLamaSharp model would use.
/// LLamaSharp 0.27 auto-selects: Metal on macOS, CUDA when an NVIDIA driver is present
/// on Windows/Linux, Vulkan as the remaining GPU option on Windows/Linux.
/// Stateless utility — no mutable state.
/// </summary>
public static class VulkanAvailabilityProbe
{
    // Stateless utility — no mutable state

    /// <summary>
    /// True when the active GPU backend for in-process models is Vulkan: a Vulkan-capable
    /// OS (Windows/Linux) with no CUDA driver present. macOS always uses Metal.
    /// </summary>
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
