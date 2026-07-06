using System.Text.Json.Serialization;

namespace BimEngine.Core.Models;

/// <summary>
/// Closed set of room kinds. Used as an enum (not a free string) on the room program and room specs
/// so the API contract constrains the field to a known vocabulary the layout + renderer understand.
/// Serialized by name.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoomType
{
    Other = 0,
    LivingRoom,
    Kitchen,
    DiningRoom,
    Bedroom,
    Bathroom,
    Hallway,
    Office,
    Laundry,
    Storage,
    Garage,
    Entrance,
    Balcony,
    Staircase,
}
