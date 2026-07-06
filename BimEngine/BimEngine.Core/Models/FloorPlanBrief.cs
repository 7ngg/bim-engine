namespace BimEngine.Core.Models;

/// <summary>
/// The rich, user-authored design brief for a building. This is the untrusted input a client
/// fills in by hand; <see cref="IRagService"/> validates it and expands it into a concrete
/// <see cref="GeometryCommand"/>. It is deliberately expressive enough to describe complex,
/// multi-floor enterprise programs (departments, zoning, adjacency rules, site constraints), and
/// the whole brief is carried onto the resulting command so a Revit renderer keeps every intent.
/// </summary>
public record FloorPlanBrief
{
    public ProjectInfo? Project { get; init; }

    /// <summary>The space program: one entry per distinct room kind (each expandable via Count).</summary>
    public List<RoomProgram> Rooms { get; init; } = [];

    public AdjacencySpec? Adjacencies { get; init; }
    public CirculationSpec? Circulation { get; init; }
    public ConstraintSpec? Constraints { get; init; }
}

/// <summary>Project-level metadata and site totals.</summary>
public record ProjectInfo
{
    public string? Name { get; init; }
    public string? BuildingType { get; init; } // "residential" | "office" | "hospital" | ...
    public int NumFloors { get; init; } = 1;
    public double? TotalAreaSqm { get; init; }
    public double? AreaTolerancePct { get; init; }
    public string Units { get; init; } = "metric";
}

/// <summary>
/// One line of the space program. <see cref="Count"/> instances of a room of <see cref="Type"/>,
/// each sized to <see cref="TargetAreaSqm"/> (falling back to <see cref="MinAreaSqm"/> or a
/// per-type default). Optional <see cref="FloorIndex"/> pins the instances to a floor; otherwise
/// they are auto-distributed. <see cref="Department"/> and the privacy/light/ensuite flags drive
/// enterprise zoning and Revit room parameters.
/// </summary>
public record RoomProgram
{
    public required string Id { get; init; } // display name / space label, e.g. "Meeting Room"
    public required RoomType Type { get; init; }
    public int Count { get; init; } = 1;
    public int? FloorIndex { get; init; } // explicit floor (0-based); null = auto-distribute

    public double? TargetAreaSqm { get; init; }
    public double? MinAreaSqm { get; init; }
    public double? MaxAreaSqm { get; init; }

    public bool IsPrimary { get; init; }
    public bool RequiresNaturalLight { get; init; }
    public bool RequiresEnsuite { get; init; }
    public string? PrivacyLevel { get; init; } // "private" | "public" | "shared"
    public string? Department { get; init; }   // enterprise zoning group, e.g. "Cardiology"

    /// <summary>Extra required neighbours by room Id (beyond the per-floor hallway hub).</summary>
    public List<string> AdjacentTo { get; init; } = [];
}

/// <summary>Program-level adjacency rules, each a group of room Ids that relate.</summary>
public record AdjacencySpec
{
    public List<List<string>> Required { get; init; } = [];
    public List<List<string>> Preferred { get; init; } = [];
    public List<List<string>> Forbidden { get; init; } = [];
}

/// <summary>Circulation + zoning intent for the renderer to honour.</summary>
public record CirculationSpec
{
    public bool RequiresHallway { get; init; } = true;
    public string? EntranceZone { get; init; }
    public List<string> PrivateZoneRooms { get; init; } = [];
    public List<string> PublicZoneRooms { get; init; } = [];
}

/// <summary>Site + massing constraints.</summary>
public record ConstraintSpec
{
    public string? BuildingShape { get; init; }
    public string? Orientation { get; init; }
    public PlotDimensions? PlotDimensions { get; init; }
    public string? Style { get; init; }
}

public record PlotDimensions
{
    public double? WidthM { get; init; }
    public double? DepthM { get; init; }
}
