namespace BimEngine.Core.Models;

/// <summary>
/// The structured, validated payload that travels through the queue. This is the contract
/// between the API (producer) and any geometry consumer (mock now, real Revit add-in later).
/// </summary>
public record GeometryCommand(
    string ProjectId,
    int FloorCount,
    List<RoomSpec> Rooms);
