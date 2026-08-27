using System.Reflection;
using LLama;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Retrieves the projector-specific media marker token used by LLamaSharp's MTMD tokenizer.
/// LLamaSharp exposes <c>StatefulExecutorBase.GetMtmdMarker()</c> as protected, so reflection is required.
/// </summary>
public static class MtmdMarkerResolver
{
    private static readonly MethodInfo? GetMarkerMethod =
        typeof(StatefulExecutorBase).GetMethod("GetMtmdMarker",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    // Stateless utility — no mutable state; resolves a constant string from the LLamaSharp executor.
    public static string GetMarkerFor(StatefulExecutorBase executor)
    {
        if (GetMarkerMethod?.Invoke(executor, null) is string marker && marker.Length > 0)
            return marker;

        throw new InvalidOperationException(
            "Could not resolve MTMD image marker via StatefulExecutorBase.GetMtmdMarker().");
    }
}
