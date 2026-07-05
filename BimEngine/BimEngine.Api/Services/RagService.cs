using BimEngine.Core.Models;

namespace BimEngine.Api.Services;

/// <summary>
/// PoC RAG service. Applies a handful of hardcoded "building norm" rules, then distributes the
/// requested rooms across floors with simple placeholder adjacency (everything hangs off a
/// per-floor hallway, mirroring a bubble-diagram hub).
/// </summary>
public sealed class RagService : IRagService
{
    // --- Hardcoded "norms" -------------------------------------------------------------------
    // TODO(RAG): Replace these constants with retrieval over indexed building-code documents.
    //   1. Ingest norm PDFs/Excel (e.g. residential code tables) into a vector store.
    //   2. Embed the incoming BuildingRequest and retrieve the relevant clauses.
    //   3. Have an LLM extract the concrete numbers (min areas, ratios, floor heights) below.
    // The rest of this class (validation + distribution) stays identical once the numbers
    // come from retrieval instead of these literals.
    private const double MinBedroomAreaSqm = 9.0;      // min habitable bedroom area
    private const double MinBathroomAreaSqm = 4.0;     // min bathroom area
    private const double MinKitchenAreaSqm = 8.0;
    private const double MinLivingAreaSqm = 14.0;
    private const double MinHallwayAreaSqm = 4.0;
    private const int MaxBedroomsPerBathroom = 3;      // bathroom-to-bedroom ratio floor
    private const double FloorHeightM = 3.0;           // reasonable residential floor height
    private const double MinPlotAreaPerFloorSqm = 25.0; // sanity: footprint must fit the program

    // --- Layout parameters -------------------------------------------------------------------
    // Simple corridor scheme: a full-width hallway spine at the bottom of each floor, with every
    // other room in a single row above it so each room shares an edge with the hallway (→ a door).
    private const double RoomDepthM = 4.0;     // depth of the room row (north-south)
    private const double HallwayDepthM = 2.0;  // depth of the hallway spine

    public GeometryCommand Enrich(BuildingRequest request)
    {
        Validate(request);

        var projectId = $"PRJ-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var rooms = DistributeRooms(request);
        var doors = DeriveDoors(rooms);

        return new GeometryCommand(projectId, request.FloorCount, rooms, FloorHeightM, doors);
    }

    // --- Norm validation ---------------------------------------------------------------------
    private static void Validate(BuildingRequest r)
    {
        if (r.FloorCount < 1)
            throw new RagValidationException("FloorCount must be at least 1.");
        if (r.Bedrooms < 1)
            throw new RagValidationException("A dwelling needs at least 1 bedroom.");
        if (r.Bathrooms < 1)
            throw new RagValidationException("A dwelling needs at least 1 bathroom.");
        if (r.PlotAreaSqm <= 0)
            throw new RagValidationException("PlotAreaSqm must be positive.");
        if (string.IsNullOrWhiteSpace(r.BuildingType))
            throw new RagValidationException("BuildingType is required.");

        // Bathroom-to-bedroom ratio: at least 1 bathroom per MaxBedroomsPerBathroom bedrooms.
        var requiredBathrooms = (int)Math.Ceiling(r.Bedrooms / (double)MaxBedroomsPerBathroom);
        if (r.Bathrooms < requiredBathrooms)
            throw new RagValidationException(
                $"{r.Bedrooms} bedrooms require at least {requiredBathrooms} bathroom(s) " +
                $"(1 per {MaxBedroomsPerBathroom} bedrooms); got {r.Bathrooms}.");

        // Footprint sanity: rough program area must plausibly fit within plot * floors.
        var minProgramArea = MinLivingAreaSqm + MinKitchenAreaSqm
            + (r.Bedrooms * MinBedroomAreaSqm)
            + (r.Bathrooms * MinBathroomAreaSqm);
        var availableArea = r.PlotAreaSqm * r.FloorCount;
        if (availableArea < minProgramArea)
            throw new RagValidationException(
                $"Plot too small: need ~{minProgramArea:0.#} sqm of floor area for the program " +
                $"but only {availableArea:0.#} sqm available across {r.FloorCount} floor(s).");

        if (r.PlotAreaSqm < MinPlotAreaPerFloorSqm)
            throw new RagValidationException(
                $"PlotAreaSqm {r.PlotAreaSqm:0.#} is below the {MinPlotAreaPerFloorSqm} sqm minimum footprint.");
    }

    // --- Room distribution -------------------------------------------------------------------
    // Simple + readable on purpose: one hallway per floor acts as the adjacency hub. Bedrooms and
    // bathrooms spread round-robin across floors; common rooms (living, kitchen) go on floor 0.
    // Every non-hallway room sits in a single row above the full-width hallway, so it shares an
    // edge with the hallway and (where placed side by side) with its row neighbours.
    private static List<RoomSpec> DistributeRooms(BuildingRequest r)
    {
        static string HallwayOn(int floor) => $"Hallway (Floor {floor})";

        // 1. Assign the program to floors WITHOUT geometry yet: (name, area, floor, adjacency).
        var byFloor = new Dictionary<int, List<(string Name, double Area, List<string> Adj)>>();
        for (var floor = 0; floor < r.FloorCount; floor++)
            byFloor[floor] = new List<(string, double, List<string>)>();

        // Common rooms live on the ground floor (living first, then kitchen beside it).
        byFloor[0].Add(("Living Room", MinLivingAreaSqm, new List<string> { HallwayOn(0) }));
        byFloor[0].Add(("Kitchen", MinKitchenAreaSqm, new List<string> { HallwayOn(0), "Living Room" }));

        // Bedrooms then bathrooms, each round-robin across floors, adjacent to their floor hallway.
        for (var i = 0; i < r.Bedrooms; i++)
        {
            var floor = i % r.FloorCount;
            byFloor[floor].Add(($"Bedroom {i + 1}", MinBedroomAreaSqm, new List<string> { HallwayOn(floor) }));
        }
        for (var i = 0; i < r.Bathrooms; i++)
        {
            var floor = i % r.FloorCount;
            byFloor[floor].Add(($"Bathroom {i + 1}", MinBathroomAreaSqm, new List<string> { HallwayOn(floor) }));
        }

        // 2. Lay out each floor and materialise RoomSpecs with concrete footprints.
        var rooms = new List<RoomSpec>();
        for (var floor = 0; floor < r.FloorCount; floor++)
        {
            var cursorX = 0.0; // running x-offset of the room row
            foreach (var (name, area, adj) in byFloor[floor])
            {
                var width = area / RoomDepthM;
                var footprint = new RoomFootprint(cursorX, HallwayDepthM, width, RoomDepthM);
                rooms.Add(new RoomSpec(name, area, floor, adj, footprint));
                cursorX += width;
            }

            // The hallway spans the full row width along the bottom of the floor.
            var hallWidth = cursorX > 0 ? cursorX : MinHallwayAreaSqm / HallwayDepthM;
            var hallFootprint = new RoomFootprint(0, 0, hallWidth, HallwayDepthM);
            rooms.Add(new RoomSpec(HallwayOn(floor), hallWidth * HallwayDepthM, floor,
                new List<string>(), hallFootprint));
        }

        return rooms;
    }

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
