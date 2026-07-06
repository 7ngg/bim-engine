using BimEngine.Core.Models;

namespace BimEngine.Api.Services;

/// <summary>
/// Validates + enriches a user-authored <see cref="FloorPlanBrief"/> into a structured
/// <see cref="GeometryCommand"/> (expands the room program, distributes it across floors, and lays
/// out concrete footprints + doors).
///
/// SEAM: today the "norms" are hardcoded constants. The real implementation retrieves norms
/// via RAG (embed the brief, retrieve relevant clauses from indexed building-code PDFs/Excel,
/// let an LLM reason over them) and returns the same GeometryCommand. Callers never change.
/// </summary>
public interface IRagService
{
    /// <summary>
    /// Enrich the brief into a geometry command.
    /// </summary>
    /// <exception cref="RagValidationException">Thrown when the brief violates a building norm.</exception>
    GeometryCommand Enrich(FloorPlanBrief brief);
}

/// <summary>Raised when a request fails norm validation. Maps to HTTP 400.</summary>
public sealed class RagValidationException(string message) : Exception(message);
