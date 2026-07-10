using System.Text.Json.Serialization;

namespace BimEngine.SpatialLayout;

/// <summary>
/// The PLAN's output, carried verbatim. <see cref="Units"/> is exactly the article's result array
/// (<c>{id, name, bbox{min_point, max_point}}</c>); <see cref="ProjectId"/> is thin transport
/// metadata (drop-file name + Revit clean-replace) and is the only field beyond the PLAN shape.
/// This is the whole payload that travels to Revit — no conversion to RoomSpec/GeometryCommand.
/// </summary>
public sealed record SpatialLayoutResult(
    string ProjectId,
    List<SpatialUnitResult> Units);

/// <summary>One placed spatial unit: its id, name, and computed 3D bounding box.</summary>
public sealed record SpatialUnitResult(
    int Id,
    string Name,
    Bbox3D Bbox);

/// <summary>
/// Axis-aligned 3D bounding box in metres. <see cref="MinPoint"/>/<see cref="MaxPoint"/> are
/// <c>[x, y, z]</c> — exactly the <c>min_point</c>/<c>max_point</c> the PLAN's Revit step reads to
/// build a mass.
/// </summary>
public sealed record Bbox3D(
    [property: JsonPropertyName("min_point")] double[] MinPoint,
    [property: JsonPropertyName("max_point")] double[] MaxPoint);
