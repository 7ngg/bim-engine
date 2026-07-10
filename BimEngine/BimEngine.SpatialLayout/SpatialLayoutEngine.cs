namespace BimEngine.SpatialLayout;

/// <summary>
/// The PLAN's <c>LayoutGenerator</c>, generalized to any number of units. Takes a
/// <see cref="SpatialLayoutRequest"/> (spatial units + relation matrix) and produces the packed
/// 3D bounding boxes as a <see cref="SpatialLayoutResult"/>.
///
/// The article hard-codes the sequence 0→1→2→3; this walks the relation graph instead: unit 0 is
/// the fixed root at the origin, then each remaining unit is placed against an already-placed unit
/// by the strongest relation it has to the placed set (containment &gt; intersection &gt; adjacency).
/// Adjacency/intersection placements get a small eastward collision sweep so unrelated units never
/// silently overlap; the mandated 1 m passage is honoured as the intersection overlap.
/// </summary>
public sealed class SpatialLayoutEngine
{
    /// <summary>Minimum passage / intersection overlap, in metres (PLAN's <c>threshold</c>).</summary>
    private const double Threshold = 1.0;

    /// <summary>Margin used when nesting one unit inside another.</summary>
    private const double Inset = 0.5;

    public SpatialLayoutResult Generate(SpatialLayoutRequest request)
    {
        Validate(request);

        var specs = request.SpatialUnits;
        var matrix = request.RelationMatrix;
        var n = specs.Count;

        // Units are indexed by their position in the list, matching the matrix rows/cols.
        var units = new SpatialUnit[n];
        for (var i = 0; i < n; i++) units[i] = new SpatialUnit(specs[i]);

        var placed = new bool[n];
        var placedOrder = new List<int>(n);

        // 1. Root stays at the origin.
        placed[0] = true;
        placedOrder.Add(0);

        // 2. Repeatedly place the unplaced unit with the strongest tie to the placed set.
        while (placedOrder.Count < n)
        {
            var (b, parent, code) = PickNext(matrix, placed, placedOrder, n);
            if (b < 0) break; // nothing left is connected to the placed set

            Place(units[b], units[parent], code);

            // Adjacency/intersection must not collide with unrelated placed units; containment is
            // an intentional overlap, so it is exempt from the sweep.
            if (code is RelationCode.Adjacency or RelationCode.Intersection)
                CollisionSweep(units, placed, matrix, b);

            placed[b] = true;
            placedOrder.Add(b);
        }

        // 3. Anything the matrix never connected to the root is parked so nothing is dropped.
        ParkDisconnected(units, placed);

        var results = new List<SpatialUnitResult>(n);
        foreach (var u in units) results.Add(u.ToResult());
        return new SpatialLayoutResult(NewProjectId(), results);
    }

    // --- Placement ---------------------------------------------------------------------------

    // Choose the next unit to place: the unplaced unit whose best relation to any placed unit is
    // strongest (containment > intersection > adjacency). Ties break to the lowest unit index, and
    // for a unit's parent, to the lowest placed index — so the layout is fully deterministic.
    private static (int Unit, int Parent, int Code) PickNext(
        int[][] matrix, bool[] placed, List<int> placedOrder, int n)
    {
        int bestUnit = -1, bestParent = -1, bestPriority = 0, bestCode = RelationCode.None;

        for (var b = 0; b < n; b++)
        {
            if (placed[b]) continue;

            // Strongest relation from any placed unit to b.
            int parent = -1, priority = 0, code = RelationCode.None;
            foreach (var a in placedOrder)
            {
                var c = matrix[a][b];
                var p = Priority(c);
                if (p > priority)
                {
                    priority = p; parent = a; code = c;
                }
            }

            if (priority == 0) continue; // b not yet connected to the placed set
            if (priority > bestPriority || (priority == bestPriority && b < bestUnit))
            {
                bestPriority = priority; bestUnit = b; bestParent = parent; bestCode = code;
            }
        }

        return (bestUnit, bestParent, bestCode);
    }

    private static void Place(SpatialUnit b, SpatialUnit a, int code)
    {
        switch (code)
        {
            case RelationCode.Intersection: b.PlaceIntersecting(a, Threshold); break;
            case RelationCode.Contains:     b.PlaceInside(a, Inset); break;      // a contains b
            case RelationCode.ContainedBy:  b.PlaceEnclosing(a, Inset); break;   // b contains a
            default:                        b.PlaceEastOf(a); break;              // adjacency (and fallback)
        }
    }

    // Shift the just-placed unit east until it no longer overlaps any placed unit it is not
    // matrix-related to. Monotonic eastward motion over a finite placed set always terminates; the
    // guard is a belt-and-braces cap.
    private static void CollisionSweep(SpatialUnit[] units, bool[] placed, int[][] matrix, int b)
    {
        var moving = units[b];
        var guard = units.Length * 4 + 8;

        while (guard-- > 0)
        {
            SpatialUnit? hit = null;
            for (var a = 0; a < units.Length; a++)
            {
                if (a == b || !placed[a]) continue;
                if (Related(matrix, a, b)) continue; // intended contact/overlap — leave it
                if (moving.Overlaps3D(units[a])) { hit = units[a]; break; }
            }

            if (hit is null) break;
            moving.Move(moving.OverlapX(hit) + 0.001, 0);
        }
    }

    // Place unplaced (disconnected) units in a row north of everything already placed.
    private static void ParkDisconnected(SpatialUnit[] units, bool[] placed)
    {
        var haveAny = false;
        var maxNorth = 0.0;
        for (var i = 0; i < units.Length; i++)
        {
            if (!placed[i]) continue;
            maxNorth = haveAny ? Math.Max(maxNorth, units[i].FaceN) : units[i].FaceN;
            haveAny = true;
        }

        var y = (haveAny ? maxNorth : 0.0) + 2.0; // 2 m gap above the packed layout
        var x = 0.0;
        for (var i = 0; i < units.Length; i++)
        {
            if (placed[i]) continue;
            var u = units[i];
            u.Move(x - u.FaceW, y - u.FaceS);
            x = u.FaceE + 1.0;
            placed[i] = true;
        }
    }

    private static bool Related(int[][] matrix, int a, int b) =>
        matrix[a][b] != RelationCode.None || matrix[b][a] != RelationCode.None;

    private static int Priority(int code) => code switch
    {
        RelationCode.Contains or RelationCode.ContainedBy => 3,
        RelationCode.Intersection => 2,
        RelationCode.Adjacency => 1,
        _ => 0,
    };

    private static string NewProjectId() =>
        $"SPL-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    // --- Validation --------------------------------------------------------------------------

    private static void Validate(SpatialLayoutRequest request)
    {
        var units = request.SpatialUnits;
        if (units is null || units.Count == 0)
            throw new SpatialLayoutValidationException("spatial_units must contain at least one unit.");

        var n = units.Count;
        var seenIds = new HashSet<int>();
        foreach (var u in units)
        {
            if (!seenIds.Add(u.Id))
                throw new SpatialLayoutValidationException($"Duplicate unit id {u.Id}.");
            if (u.Area <= 0)
                throw new SpatialLayoutValidationException($"Unit {u.Id} ('{u.Name}') area must be > 0.");
            if (u.Ratio <= 0)
                throw new SpatialLayoutValidationException($"Unit {u.Id} ('{u.Name}') ratio must be > 0.");
            if (u.Height <= 0)
                throw new SpatialLayoutValidationException($"Unit {u.Id} ('{u.Name}') height must be > 0.");
        }

        var matrix = request.RelationMatrix;
        if (matrix is null || matrix.Length != n)
            throw new SpatialLayoutValidationException(
                $"relation_matrix must be {n}x{n} to match the {n} spatial_units.");

        for (var i = 0; i < n; i++)
        {
            var row = matrix[i];
            if (row is null || row.Length != n)
                throw new SpatialLayoutValidationException(
                    $"relation_matrix row {i} must have {n} entries.");
            for (var j = 0; j < n; j++)
                if (row[j] < RelationCode.None || row[j] > RelationCode.Max)
                    throw new SpatialLayoutValidationException(
                        $"relation_matrix[{i}][{j}] = {row[j]} is not a valid code (0..{RelationCode.Max}).");
        }
    }
}
