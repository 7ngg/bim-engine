namespace BimEngine.Core.Models;

/// <summary>
/// Stage-2 Gemini output: a set of alternative floor-plan layouts for one brief. The model solves
/// only geometry here — it does NOT re-emit the extracted brief (the service injects the stage-1
/// <see cref="FloorPlanExtraction"/> into each resulting <see cref="GeometryCommand"/>).
/// </summary>
public record LayoutVariants
{
    public List<FloorPlanVariant> Variants { get; init; } = [];
}

/// <summary>
/// One concrete layout candidate. Maps almost 1:1 onto <see cref="GeometryCommand"/>; the server
/// assigns the <c>ProjectId</c> and attaches the shared brief during mapping.
/// </summary>
public record FloorPlanVariant
{
    public string? Label { get; init; }   // e.g. "open-plan", "compact"
    public int FloorCount { get; init; }
    public double FloorHeightM { get; init; } = 3.0;
    public List<RoomSpec> Rooms { get; init; } = [];
    public List<DoorSpec> Doors { get; init; } = [];
}
