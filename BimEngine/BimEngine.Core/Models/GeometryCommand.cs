namespace BimEngine.Core.Models;

/// <summary>
/// The structured, validated payload that travels through the queue. This is the contract
/// between the API (producer) and any geometry consumer (mock now, real Revit add-in later).
/// <see cref="Doors"/>, <see cref="FloorHeightM"/>, and <see cref="Brief"/> are additive/defaulted
/// so older consumers keep working.
/// </summary>
public record GeometryCommand(
    string ProjectId,
    int FloorCount,
    List<RoomSpec> Rooms,
    double FloorHeightM = 3.0,
    List<DoorSpec>? Doors = null,
    // Full stage-1 design brief (ProjectInfo, Constraints incl. orientation/plot/style, Adjacencies,
    // Circulation, RoomSummary, ExtractionMeta). Carried whole so nothing useful to Revit is lost;
    // null on the classic BuildingRequest path.
    FloorPlanExtraction? Brief = null);

/// <summary>
/// A door on the shared wall between two rooms, located at a floor-local point in metres.
/// Derived by the RAG service from <see cref="RoomSpec.AdjacentTo"/>.
/// </summary>
public record DoorSpec(
    string FromRoom,
    string ToRoom,
    double CenterXm,
    double CenterYm,
    int FloorIndex);
