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

    public GeometryCommand Enrich(BuildingRequest request)
    {
        Validate(request);

        var projectId = $"PRJ-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var rooms = DistributeRooms(request);

        return new GeometryCommand(projectId, request.FloorCount, rooms);
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

        // FloorHeightM is an assumed norm carried for downstream geometry (not validated here);
        // referenced so the vertical extent is explicit in the PoC.
        _ = FloorHeightM;
    }

    // --- Room distribution -------------------------------------------------------------------
    // Simple + readable on purpose: one hallway per floor acts as the adjacency hub. Bedrooms and
    // bathrooms spread round-robin across floors; common rooms (living, kitchen) go on floor 0.
    private static List<RoomSpec> DistributeRooms(BuildingRequest r)
    {
        var rooms = new List<RoomSpec>();

        // One hallway per floor — the hub every other room is adjacent to.
        for (var floor = 0; floor < r.FloorCount; floor++)
        {
            rooms.Add(new RoomSpec($"Hallway (Floor {floor})", MinHallwayAreaSqm, floor, new List<string>()));
        }

        string HallwayOn(int floor) => $"Hallway (Floor {floor})";

        // Common rooms live on the ground floor.
        rooms.Add(new RoomSpec("Living Room", MinLivingAreaSqm, 0, new List<string> { HallwayOn(0) }));
        rooms.Add(new RoomSpec("Kitchen", MinKitchenAreaSqm, 0, new List<string> { HallwayOn(0), "Living Room" }));

        // Bedrooms spread round-robin across floors, each adjacent to its floor's hallway.
        for (var i = 0; i < r.Bedrooms; i++)
        {
            var floor = i % r.FloorCount;
            rooms.Add(new RoomSpec($"Bedroom {i + 1}", MinBedroomAreaSqm, floor,
                new List<string> { HallwayOn(floor) }));
        }

        // Bathrooms spread round-robin across floors too.
        for (var i = 0; i < r.Bathrooms; i++)
        {
            var floor = i % r.FloorCount;
            rooms.Add(new RoomSpec($"Bathroom {i + 1}", MinBathroomAreaSqm, floor,
                new List<string> { HallwayOn(floor) }));
        }

        return rooms;
    }
}
