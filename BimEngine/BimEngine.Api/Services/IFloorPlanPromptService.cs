using BimEngine.Core.Models;

namespace BimEngine.Api.Services;

/// <summary>
/// Turns a natural-language building brief into a set of buildable floor-plan variants via a
/// two-stage Gemini pipeline (extract structured params → generate N layouts), returning ready-to-
/// publish <see cref="GeometryCommand"/>s. Keeps all Gemini/HTTP concerns out of the controller.
/// </summary>
public interface IFloorPlanPromptService
{
    /// <summary>Extract params from <paramref name="prompt"/> and generate layout variants.</summary>
    /// <exception cref="FloorPlanConfigException">Thrown when GEMINI_API_KEY is not configured.</exception>
    /// <exception cref="FloorPlanUpstreamException">Thrown when Gemini errors or returns unusable output.</exception>
    Task<IReadOnlyList<GeometryCommand>> GenerateVariantsAsync(string prompt, CancellationToken ct);
}

/// <summary>Missing/invalid service configuration. Maps to HTTP 500.</summary>
public sealed class FloorPlanConfigException(string message) : Exception(message);

/// <summary>Gemini call failed or returned an unparseable/empty plan. Maps to HTTP 502.</summary>
public sealed class FloorPlanUpstreamException(string message) : Exception(message);
