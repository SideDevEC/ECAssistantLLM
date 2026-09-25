using ECAssistantInference.Abstractions;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Resolves the MTMD image marker for vision prompts.
/// With ECAssistantInference, the marker comes from IVisionEncoder.Marker.
/// </summary>
public static class MtmdMarkerResolver
{
    /// <summary>Get the media marker from the vision encoder, or default placeholder.</summary>
    public static string GetMarker(IVisionEncoder? vision)
    {
        if (vision != null)
            return vision.Marker;
        return "<__media__>";
    }
}
