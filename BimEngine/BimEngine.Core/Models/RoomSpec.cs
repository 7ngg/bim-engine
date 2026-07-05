namespace BimEngine.Core.Models;

/// <summary>
/// A single room in the generated design. <see cref="AdjacentTo"/> captures the
/// bubble-diagram adjacency relationships that a real Revit add-in would use to place
/// walls, doors and openings.
/// </summary>
public record RoomSpec(
    string Name,
    double AreaSqm,
    int FloorIndex,
    List<string> AdjacentTo);
