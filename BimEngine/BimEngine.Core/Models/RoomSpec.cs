namespace BimEngine.Core.Models;

/// <summary>
/// A single room in the generated design. <see cref="AdjacentTo"/> captures the
/// bubble-diagram adjacency relationships that a real Revit add-in uses to place doors.
/// <see cref="Footprint"/> is the concrete floor-local rectangle computed upstream by the
/// RAG service; it is nullable so callers that only care about the program (like the mock
/// consumer) stay unaffected.
/// </summary>
public record RoomSpec(
    string Name,
    double AreaSqm,
    int FloorIndex,
    List<string> AdjacentTo,
    RoomFootprint? Footprint = null,
    // --- enrichment (defaulted, additive) ------------------------------------------------------
    // Concrete per-room semantics carried from the extracted brief so the Revit renderer can act on
    // them (room department/name, exterior placement + window, ensuite grouping, sizing target).
    RoomType? Type = null,             // constrained room kind (see RoomType)
    string? PrivacyLevel = null,       // "private" | "public" | "shared"
    bool RequiresNaturalLight = false, // → place on an exterior wall, add a window
    bool RequiresEnsuite = false,
    bool IsPrimary = false,
    double? TargetAreaSqm = null);

/// <summary>
/// Concrete rectangular footprint of a room, in metres, relative to its floor's origin.
/// Computed by the RAG service so the Revit add-in can stay a thin renderer (it just draws
/// the rectangle rather than solving layout itself).
/// </summary>
public record RoomFootprint(
    double OriginXm,
    double OriginYm,
    double WidthM,
    double DepthM);
