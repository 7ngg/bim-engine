namespace BimEngine.Core.Models;

/// <summary>
/// Raw building parameters supplied by a client. This is the untrusted input that the
/// RAG-style service validates and enriches before any geometry is produced.
/// </summary>
public record BuildingRequest(
    int FloorCount,
    int Bedrooms,
    int Bathrooms,
    double PlotAreaSqm,
    string BuildingType);
