using System.Text.Json.Serialization;

namespace BimEngine.Core.Models;

/// <summary>
/// Closed set of room kinds. Used as an enum (not a free string) on the room specs so the JSON
/// schema sent to Gemini constrains the field to these values — this prevents the token-repetition
/// loops the model otherwise falls into on an open string field. Serialized by name.
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
