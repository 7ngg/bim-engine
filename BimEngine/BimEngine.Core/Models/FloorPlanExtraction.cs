namespace BimEngine.Core.Models;

public record FloorPlanExtraction
{
    public ProjectInfo? Project { get; init; }
    public List<RoomSpecs> Rooms { get; init; } = [];
    public Dictionary<string, int> RoomSummary { get; init; } = [];
    public AdjacencySpec? Adjacencies { get; init; }
    public CirculationSpec? Circulation { get; init; }
    public ConstraintSpec? Constraints { get; init; }
    public ExtractionMeta? ExtractionMeta { get; init; }
}

public record ProjectInfo
{
    public double? TotalAreaSqm { get; init; }
    public double? AreaTolerancePct { get; init; }
    public int? NumFloors { get; init; }
    public string Units { get; init; } = "metric";
    public string? BuildingType { get; init; }
}

public record RoomSpecs
{
    public required string Id { get; init; }
    public required RoomType Type { get; init; }
    public int Count { get; init; } = 1;
    public bool IsPrimary { get; init; }
    public double? MinAreaSqm { get; init; }
    public double? TargetAreaSqm { get; init; }
    public double? MaxAreaSqm { get; init; }
    public bool RequiresNaturalLight { get; init; }
    public bool RequiresEnsuite { get; init; }
    public string? PrivacyLevel { get; init; } // "private" | "public" | "shared"
}

public record AdjacencySpec
{
    public List<List<string>> Required { get; init; } = [];
    public List<List<string>> Preferred { get; init; } = [];
    public List<List<string>> Forbidden { get; init; } = [];
}

public record CirculationSpec
{
    public bool RequiresHallway { get; init; }
    public string? EntranceZone { get; init; }
    public List<string> PrivateZoneRooms { get; init; } = [];
    public List<string> PublicZoneRooms { get; init; } = [];
}

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

public record ExtractionMeta
{
    public string Confidence { get; init; } = "unknown"; // "low" | "medium" | "high"
    public List<string> Assumptions { get; init; } = [];
    public List<string> MissingCriticalInfo { get; init; } = [];
}