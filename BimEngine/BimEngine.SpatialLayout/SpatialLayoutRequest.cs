using System.Text.Json.Serialization;

namespace BimEngine.SpatialLayout;

/// <summary>
/// The compact input an LLM emits for the PLAN pipeline — accepted verbatim in the shape the PLAN
/// prescribes: a list of spatial units plus a square relation matrix (rows/cols aligned with the
/// unit order). The snake_case wire names are pinned with <see cref="JsonPropertyNameAttribute"/>
/// so the exact PLAN JSON binds regardless of the host's naming policy.
/// </summary>
public sealed record SpatialLayoutRequest(
    [property: JsonPropertyName("spatial_units")] List<SpatialUnitSpec> SpatialUnits,
    [property: JsonPropertyName("relation_matrix")] int[][] RelationMatrix);

/// <summary>
/// One spatial unit as authored. <see cref="Area"/> and <see cref="Ratio"/> drive the computed
/// width/length; <see cref="Height"/> and <see cref="LevelHeight"/> drive the z extent
/// (base <c>= LevelHeight</c>, top <c>= LevelHeight + Height</c>). Field names already match the
/// PLAN JSON under camelCase (<c>id/name/area/ratio/height/levelHeight</c>).
/// </summary>
public sealed record SpatialUnitSpec(
    int Id,
    string Name,
    double Area,
    double Ratio,
    double Height,
    double LevelHeight);
