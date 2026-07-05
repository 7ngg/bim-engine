using BimEngine.Core.Models;

namespace BimEngine.Api.Services;

/// <summary>
/// Validates + enriches a raw <see cref="BuildingRequest"/> into a structured
/// <see cref="GeometryCommand"/>.
///
/// SEAM: today the "norms" are hardcoded constants. The real implementation retrieves norms
/// via RAG (embed the request, retrieve relevant clauses from indexed building-code PDFs/Excel,
/// let an LLM reason over them) and returns the same GeometryCommand. Callers never change.
/// </summary>
public interface IRagService
{
    /// <summary>
    /// Enrich the request into a geometry command.
    /// </summary>
    /// <exception cref="RagValidationException">Thrown when the request violates a building norm.</exception>
    GeometryCommand Enrich(BuildingRequest request);
}

/// <summary>Raised when a request fails norm validation. Maps to HTTP 400.</summary>
public sealed class RagValidationException(string message) : Exception(message);
