using BimEngine.Core.Models;

namespace BimEngine.Api.Services;

/// <summary>
/// PoC RAG service. Validates a user-authored <see cref="FloorPlanBrief"/> against a handful of
/// hardcoded "building norms", expands its room program (Count → instances) across floors, then
/// lays each floor out with a simple placeholder scheme: a full-width hallway spine with every
/// other room in a row above it, so each room shares an edge with the hallway (→ a door). The rich
/// brief is carried through onto the command so a real Revit renderer keeps every design intent.
/// </summary>
public sealed class RagService : IRagService
{
    // --- Hardcoded "norms" -------------------------------------------------------------------
    // TODO(RAG): Replace these constants with retrieval over indexed building-code documents.
    //   1. Ingest norm PDFs/Excel (e.g. residential + commercial code tables) into a vector store.
    //   2. Embed the incoming FloorPlanBrief and retrieve the relevant clauses.
    //   3. Have an LLM extract the concrete numbers (min areas, ratios, floor heights) below.
    // The rest of this class (validation + layout) stays identical once the numbers come from
    // retrieval instead of these literals.
    private const double DefaultFloorHeightM = 3.0;
    private const int MaxCountPerProgram = 500;         // sanity ceiling on Count expansion

    // Per-type minimum habitable area (sqm). Also the fallback size when a program gives no area.
    private static readonly IReadOnlyDictionary<RoomType, double> MinAreaByType = new Dictionary<RoomType, double>
    {
        [RoomType.LivingRoom] = 14.0,
        [RoomType.Kitchen] = 8.0,
        [RoomType.DiningRoom] = 8.0,
        [RoomType.Bedroom] = 9.0,
        [RoomType.Bathroom] = 4.0,
        [RoomType.Hallway] = 4.0,
        [RoomType.Office] = 6.0,
        [RoomType.Laundry] = 3.0,
        [RoomType.Storage] = 2.0,
        [RoomType.Garage] = 12.0,
        [RoomType.Entrance] = 3.0,
        [RoomType.Balcony] = 3.0,
        [RoomType.Staircase] = 4.0,
        [RoomType.Other] = 6.0,
    };

    // --- Layout parameters -------------------------------------------------------------------
    // Simple corridor scheme: a full-width hallway spine at the bottom of each floor, with every
    // other room in a single row above it so each room shares an edge with the hallway (→ a door).
    private const double RoomDepthM = 4.0;     // depth of the room row (north-south)
    private const double HallwayDepthM = 2.0;  // depth of the hallway spine

    public GeometryCommand Enrich(FloorPlanBrief brief)
    {
        var floorCount = ResolveFloorCount(brief);
        Validate(brief, floorCount);

        var projectId = $"PRJ-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var rooms = LayOut(brief, floorCount);
        var doors = DeriveDoors(rooms);

        return new GeometryCommand(projectId, floorCount, rooms, DefaultFloorHeightM, doors, Brief: brief);
    }

    // Floors span the greatest of: the declared NumFloors, any explicit per-room FloorIndex, and 1.
    private static int ResolveFloorCount(FloorPlanBrief brief)
    {
        var declared = brief.Project?.NumFloors ?? 1;
        var maxExplicit = brief.Rooms
            .Where(r => r.FloorIndex is >= 0)
            .Select(r => r.FloorIndex!.Value + 1)
            .DefaultIfEmpty(1)
            .Max();
        return Math.Max(Math.Max(declared, maxExplicit), 1);
    }

    // Resolved size of a program's rooms: explicit target, else min, else the per-type default.
    private static double ResolveArea(RoomProgram p) =>
        p.TargetAreaSqm ?? p.MinAreaSqm ?? MinAreaByType[p.Type];

    // --- Norm validation ---------------------------------------------------------------------
    private static void Validate(FloorPlanBrief brief, int floorCount)
    {
        if (brief.Rooms.Count == 0)
            throw new RagValidationException("The brief must contain at least one room in its program.");

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in brief.Rooms)
        {
            if (string.IsNullOrWhiteSpace(p.Id))
                throw new RagValidationException("Every room program entry needs a non-empty Id.");
            if (!seenIds.Add(p.Id))
                throw new RagValidationException($"Duplicate room Id '{p.Id}' in the program.");
            if (p.Count is < 1 or > MaxCountPerProgram)
                throw new RagValidationException($"Room '{p.Id}' Count must be between 1 and {MaxCountPerProgram}.");
            if (p.FloorIndex is { } fi && (fi < 0 || fi >= floorCount))
                throw new RagValidationException(
                    $"Room '{p.Id}' FloorIndex {fi} is outside the building's {floorCount} floor(s).");

            var minArea = MinAreaByType[p.Type];
            var area = ResolveArea(p);
            if (area < minArea)
                throw new RagValidationException(
                    $"Room '{p.Id}' area {area:0.#} sqm is below the {minArea:0.#} sqm minimum for a {p.Type}.");
            if (p.MaxAreaSqm is { } max && area > max)
                throw new RagValidationException(
                    $"Room '{p.Id}' area {area:0.#} sqm exceeds its own MaxAreaSqm {max:0.#}.");
        }

        // Footprint sanity: the whole program must plausibly fit within plot * floors, when a plot
        // is given. (Without a plot we cannot bound it — a real renderer would size the plot instead.)
        var plot = brief.Constraints?.PlotDimensions;
        if (plot?.WidthM is > 0 && plot.DepthM is > 0)
        {
            var available = plot.WidthM.Value * plot.DepthM.Value * floorCount;
            var required = brief.Rooms.Sum(p => ResolveArea(p) * p.Count);
            if (available < required)
                throw new RagValidationException(
                    $"Plot too small: program needs ~{required:0.#} sqm of floor area but only " +
                    $"{available:0.#} sqm is available across {floorCount} floor(s).");
        }
    }

    // --- Program expansion + layout ----------------------------------------------------------
    // Expand each program into Count instances, assign every instance to a floor (explicit, else
    // round-robin), then pack each floor as two room rows flanking a central full-width corridor
    // (a "double-loaded corridor"): every room reaches the corridor for a door, and adjacency
    // ordering keeps required neighbours in adjacent slices so they share a wall too.
    private static List<RoomSpec> LayOut(FloorPlanBrief brief, int floorCount)
    {
        static string HallwayOn(int floor) => $"Hallway (Floor {floor})";
        var wantHallway = brief.Circulation?.RequiresHallway ?? true;

        // 1. Expand the program into placed instances, WITHOUT geometry yet.
        var byFloor = new Dictionary<int, List<Instance>>();
        for (var f = 0; f < floorCount; f++) byFloor[f] = [];

        // Required-adjacency groups + per-room AdjacentTo → a symmetric neighbour map keyed by Id.
        var neighbours = BuildNeighbourMap(brief);

        var autoCursor = 0; // round-robin floor pointer for rooms with no explicit FloorIndex
        foreach (var p in brief.Rooms)
        {
            var area = ResolveArea(p);
            for (var i = 0; i < p.Count; i++)
            {
                var name = p.Count == 1 ? p.Id : $"{p.Id} {i + 1}";
                var floor = p.FloorIndex ?? (autoCursor++ % floorCount);
                var adj = new List<string>();
                if (wantHallway) adj.Add(HallwayOn(floor));
                if (neighbours.TryGetValue(p.Id, out var extra)) adj.AddRange(extra);

                byFloor[floor].Add(new Instance(name, p, area, floor, adj));
            }
        }

        // 2. Pack each floor and materialise enriched RoomSpecs with concrete footprints.
        var rooms = new List<RoomSpec>();
        for (var floor = 0; floor < floorCount; floor++)
        {
            var onFloor = byFloor[floor];
            if (onFloor.Count == 0) continue;

            var (plotW, plotD) = ResolvePlot(brief, onFloor);
            var hc = HallwayDepthM;
            var sideDepth = (plotD - hc) / 2;              // depth of each room band
            var topY = (plotD + hc) / 2;                   // bottom edge of the top band
            var corridorY = (plotD - hc) / 2;              // bottom edge of the corridor

            var ordered = OrderByAdjacency(onFloor, neighbours, brief.Circulation);
            var (top, bottom) = SplitRows(ordered, sideDepth);

            void AddRoom(Instance inst, RoomFootprint fp)
            {
                var p = inst.Program;
                rooms.Add(new RoomSpec(
                    inst.Name, inst.Area, floor, inst.Adjacent, fp,
                    Type: p.Type,
                    PrivacyLevel: p.PrivacyLevel,
                    RequiresNaturalLight: p.RequiresNaturalLight,
                    RequiresEnsuite: p.RequiresEnsuite,
                    IsPrimary: p.IsPrimary,
                    TargetAreaSqm: p.TargetAreaSqm));
            }

            var xTop = 0.0;
            foreach (var inst in top)
            {
                var w = inst.Area / sideDepth;
                AddRoom(inst, new RoomFootprint(xTop, topY, w, sideDepth));
                xTop += w;
            }

            var xBot = 0.0;
            foreach (var inst in bottom)
            {
                var w = inst.Area / sideDepth;
                AddRoom(inst, new RoomFootprint(xBot, 0, w, sideDepth));
                xBot += w;
            }

            if (wantHallway)
            {
                // Corridor spans the full plot / widest occupied row so every room reaches it.
                var corridorW = Math.Max(plotW, Math.Max(xTop, xBot));
                rooms.Add(new RoomSpec(HallwayOn(floor), corridorW * hc, floor,
                    [], new RoomFootprint(0, corridorY, corridorW, hc), Type: RoomType.Hallway));
            }
        }

        return rooms;
    }

    // --- Plot + packing helpers --------------------------------------------------------------

    // Resolve the plot rectangle (metres) for one floor's rooms. Honour an explicit plot when both
    // dimensions are given (widening it if the program can't fit two rows into the stated width);
    // otherwise derive a square-ish plot sized so the median room comes out roughly square.
    private static (double Width, double Depth) ResolvePlot(FloorPlanBrief brief, List<Instance> onFloor)
    {
        var totalArea = onFloor.Sum(i => i.Area);
        var median = MedianArea(onFloor);
        // Working band depth so a median-area room is ~square (side ≈ sqrt(area)).
        var sideDepth = Math.Clamp(Math.Sqrt(median), 3.0, 6.0);

        var plot = brief.Constraints?.PlotDimensions;
        if (plot?.WidthM is > 0 && plot.DepthM is > 0)
        {
            var depth = plot.DepthM.Value;
            var band = (depth - HallwayDepthM) / 2;
            if (band < 1.0) // too shallow for two rows + corridor → grow depth to fit
            {
                band = sideDepth;
                depth = 2 * band + HallwayDepthM;
            }
            // Widen if the stated width can't hold two rows of the program area.
            var needed = totalArea / (2 * band);
            return (Math.Max(plot.WidthM.Value, needed), depth);
        }

        var width = totalArea / (2 * sideDepth);
        return (Math.Max(width, sideDepth), 2 * sideDepth + HallwayDepthM);
    }

    private static double MedianArea(List<Instance> onFloor)
    {
        var areas = onFloor.Select(i => i.Area).OrderBy(a => a).ToList();
        return areas.Count == 0 ? 0 : areas[areas.Count / 2];
    }

    // Order rooms so required neighbours sit next to each other (→ shared wall → door), biased so
    // public-zone rooms come first and private-zone rooms last. Greedy walk of the neighbour graph:
    // seed with the lowest-zone unplaced room, then keep appending an unplaced neighbour of the last
    // room until the run ends, then reseed.
    private static List<Instance> OrderByAdjacency(
        List<Instance> onFloor,
        Dictionary<string, HashSet<string>> neighbours,
        CirculationSpec? circ)
    {
        var publicRooms = new HashSet<string>(circ?.PublicZoneRooms ?? [], StringComparer.OrdinalIgnoreCase);
        var privateRooms = new HashSet<string>(circ?.PrivateZoneRooms ?? [], StringComparer.OrdinalIgnoreCase);
        int Zone(Instance i) => publicRooms.Contains(i.Program.Id) ? 0 : privateRooms.Contains(i.Program.Id) ? 2 : 1;

        bool Linked(Instance a, Instance b) =>
            neighbours.TryGetValue(a.Program.Id, out var set) && set.Contains(b.Program.Id);

        var remaining = onFloor.OrderBy(Zone).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
        var ordered = new List<Instance>(remaining.Count);

        while (remaining.Count > 0)
        {
            var current = remaining[0]; // lowest-zone unplaced room
            remaining.RemoveAt(0);
            ordered.Add(current);

            // Extend the run through linked, unplaced neighbours (lowest zone first).
            while (true)
            {
                var next = remaining
                    .Where(r => Linked(current, r))
                    .OrderBy(Zone).ThenBy(r => r.Name, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (next is null) break;
                remaining.Remove(next);
                ordered.Add(next);
                current = next;
            }
        }

        return ordered;
    }

    // Split the ordered rooms into a top and bottom band, adding each room to whichever side is
    // currently narrower so the two rows stay balanced around the corridor.
    private static (List<Instance> Top, List<Instance> Bottom) SplitRows(
        List<Instance> ordered, double sideDepth)
    {
        var top = new List<Instance>();
        var bottom = new List<Instance>();
        double wTop = 0, wBot = 0;

        foreach (var inst in ordered)
        {
            var w = inst.Area / sideDepth;
            if (wTop <= wBot) { top.Add(inst); wTop += w; }
            else { bottom.Add(inst); wBot += w; }
        }

        return (top, bottom);
    }

    // Fold RoomProgram.AdjacentTo and AdjacencySpec.Required into one symmetric Id→neighbours map.
    // Door derivation later only realises a neighbour when the two footprints actually touch, so an
    // unmatched or multi-instance neighbour is simply harmless intent.
    private static Dictionary<string, HashSet<string>> BuildNeighbourMap(FloorPlanBrief brief)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Link(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return;
            (map.TryGetValue(a, out var set) ? set : map[a] = []).Add(b);
        }

        foreach (var p in brief.Rooms)
            foreach (var other in p.AdjacentTo)
            {
                Link(p.Id, other);
                Link(other, p.Id);
            }

        foreach (var group in brief.Adjacencies?.Required ?? [])
            foreach (var a in group)
                foreach (var b in group)
                    Link(a, b);

        return map;
    }

    // An expanded, floor-assigned room instance before geometry is computed.
    private sealed record Instance(string Name, RoomProgram Program, double Area, int Floor, List<string> Adjacent);

    // --- Door derivation ---------------------------------------------------------------------
    // A door exists wherever two adjacent rooms on the same floor share a wall edge.
    private static List<DoorSpec> DeriveDoors(List<RoomSpec> rooms)
    {
        var byName = rooms.ToDictionary(room => room.Name);
        var seen = new HashSet<string>();
        var doors = new List<DoorSpec>();

        foreach (var room in rooms)
        {
            if (room.Footprint is null) continue;

            foreach (var neighbourName in room.AdjacentTo)
            {
                if (!byName.TryGetValue(neighbourName, out var neighbour)) continue;
                if (neighbour.Footprint is null || neighbour.FloorIndex != room.FloorIndex) continue;

                // Unordered pair key so each shared wall gets at most one door.
                var key = string.CompareOrdinal(room.Name, neighbourName) < 0
                    ? $"{room.Name}|{neighbourName}"
                    : $"{neighbourName}|{room.Name}";
                if (!seen.Add(key)) continue;

                if (TryFindSharedEdge(room.Footprint, neighbour.Footprint, out var cx, out var cy))
                    doors.Add(new DoorSpec(room.Name, neighbourName, cx, cy, room.FloorIndex));
                else
                    seen.Remove(key); // no shared edge → leave the pair open for another match
            }
        }

        return doors;
    }

    // Returns the midpoint of the shared edge between two axis-aligned rectangles, if any.
    private static bool TryFindSharedEdge(RoomFootprint a, RoomFootprint b, out double cx, out double cy)
    {
        const double eps = 1e-6;
        cx = cy = 0;

        double aL = a.OriginXm, aR = a.OriginXm + a.WidthM, aB = a.OriginYm, aT = a.OriginYm + a.DepthM;
        double bL = b.OriginXm, bR = b.OriginXm + b.WidthM, bB = b.OriginYm, bT = b.OriginYm + b.DepthM;

        // Vertical shared edge (rooms side by side): a.right == b.left or a.left == b.right.
        if (Math.Abs(aR - bL) < eps || Math.Abs(aL - bR) < eps)
        {
            var yLo = Math.Max(aB, bB);
            var yHi = Math.Min(aT, bT);
            if (yHi - yLo > eps)
            {
                cx = Math.Abs(aR - bL) < eps ? aR : aL;
                cy = (yLo + yHi) / 2;
                return true;
            }
        }

        // Horizontal shared edge (room above hallway): a.top == b.bottom or a.bottom == b.top.
        if (Math.Abs(aT - bB) < eps || Math.Abs(aB - bT) < eps)
        {
            var xLo = Math.Max(aL, bL);
            var xHi = Math.Min(aR, bR);
            if (xHi - xLo > eps)
            {
                cy = Math.Abs(aT - bB) < eps ? aT : aB;
                cx = (xLo + xHi) / 2;
                return true;
            }
        }

        return false;
    }
}
