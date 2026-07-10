namespace BimEngine.SpatialLayout;

/// <summary>
/// A single spatial unit during layout — the C# port of the PLAN's <c>SpatialUnit</c> class.
///
/// Geometry follows the article exactly: <see cref="Width"/> <c>= sqrt(Area * Ratio)</c> is the
/// north-south (y) extent, <see cref="Length"/> <c>= Area / Width</c> is the east-west (x) extent.
/// The box is tracked as six face positions in metres — West/East on x, South/North on y,
/// Bottom/Top on z — and mutated by <see cref="Move"/> / the <c>Place*</c> operators as units are
/// packed against each other.
/// </summary>
public sealed class SpatialUnit
{
    public int Id { get; }
    public string Name { get; }
    public double Area { get; private set; }
    public double Ratio { get; }

    /// <summary>North-south (y) extent, <c>sqrt(Area * Ratio)</c>.</summary>
    public double Width { get; private set; }

    /// <summary>East-west (x) extent, <c>Area / Width</c>.</summary>
    public double Length { get; private set; }

    // Six face positions (metres). x: W..E, y: S..N, z: B..T.
    public double FaceW { get; private set; } // x_min (West)
    public double FaceE { get; private set; } // x_max (East)
    public double FaceS { get; private set; } // y_min (South)
    public double FaceN { get; private set; } // y_max (North)
    public double FaceB { get; private set; } // z_min (Bottom)
    public double FaceT { get; private set; } // z_max (Top)

    public SpatialUnit(SpatialUnitSpec spec)
    {
        Id = spec.Id;
        Name = spec.Name;
        Area = spec.Area;
        Ratio = spec.Ratio;

        // Article's geometric equations.
        Width = Math.Sqrt(Area * Ratio);
        Length = Area / Width;

        // Start at the origin (0,0), sitting on its own level.
        FaceW = 0.0;
        FaceE = Length;
        FaceS = 0.0;
        FaceN = Width;
        FaceB = spec.LevelHeight;
        FaceT = FaceB + spec.Height;
    }

    /// <summary>Translate on the ground plane without changing size (PLAN <c>move</c>).</summary>
    public void Move(double dx, double dy)
    {
        FaceW += dx; FaceE += dx;
        FaceS += dy; FaceN += dy;
    }

    /// <summary>
    /// Pull the south wall to <paramref name="targetY"/> (north fixed), if the unit is at least
    /// <paramref name="threshold"/> tall. Faithful to the PLAN's <c>stretch_south</c>; retained as a
    /// constraint-refinement operator (the generalized generator packs with <see cref="Move"/> +
    /// the <c>Place*</c> operators, so this is available but not on the default path).
    /// </summary>
    public void StretchSouth(double targetY, double threshold = 1.0)
    {
        if (FaceT - FaceB >= threshold)
        {
            FaceS = targetY;
            Width = FaceN - FaceS;
            Area = Width * Length;
        }
    }

    /// <summary>Adjacency: sit immediately east of <paramref name="a"/>, south-aligned (walls touch).</summary>
    public void PlaceEastOf(SpatialUnit a, double gap = 0.0) =>
        Move((a.FaceE + gap) - FaceW, a.FaceS - FaceS);

    /// <summary>Intersection: east of <paramref name="a"/> but pulled back to overlap by <paramref name="threshold"/>.</summary>
    public void PlaceIntersecting(SpatialUnit a, double threshold) =>
        Move((a.FaceE - threshold) - FaceW, a.FaceS - FaceS);

    /// <summary>
    /// Containment (a contains this): nest inside <paramref name="a"/> with an <paramref name="inset"/>
    /// margin, shrinking the footprint if it would not otherwise fit. Own z (level) is preserved.
    /// </summary>
    public void PlaceInside(SpatialUnit a, double inset)
    {
        var availLen = (a.FaceE - a.FaceW) - 2 * inset;
        var availWid = (a.FaceN - a.FaceS) - 2 * inset;
        var len = Math.Min(Length, Math.Max(0.1, availLen));
        var wid = Math.Min(Width, Math.Max(0.1, availWid));

        FaceW = a.FaceW + inset; FaceE = FaceW + len;
        FaceS = a.FaceS + inset; FaceN = FaceS + wid;
        Length = len; Width = wid; Area = len * wid;
    }

    /// <summary>
    /// Inverse containment (this contains a): grow to enclose <paramref name="a"/> with a
    /// <paramref name="margin"/> on every side, extending the z range to cover it too.
    /// </summary>
    public void PlaceEnclosing(SpatialUnit a, double margin)
    {
        FaceW = a.FaceW - margin; FaceE = a.FaceE + margin;
        FaceS = a.FaceS - margin; FaceN = a.FaceN + margin;
        Length = FaceE - FaceW; Width = FaceN - FaceS; Area = Length * Width;
        FaceB = Math.Min(FaceB, a.FaceB);
        FaceT = Math.Max(FaceT, a.FaceT);
    }

    /// <summary>True if this box and <paramref name="o"/> overlap in all three axes (AABB test).</summary>
    public bool Overlaps3D(SpatialUnit o, double eps = 1e-9) =>
        FaceW < o.FaceE - eps && FaceE > o.FaceW + eps &&
        FaceS < o.FaceN - eps && FaceN > o.FaceS + eps &&
        FaceB < o.FaceT - eps && FaceT > o.FaceB + eps;

    /// <summary>Length of the x-overlap with <paramref name="o"/> (0 if none) — drives the collision push.</summary>
    public double OverlapX(SpatialUnit o) =>
        Math.Max(0.0, Math.Min(FaceE, o.FaceE) - Math.Max(FaceW, o.FaceW));

    /// <summary>Freeze the current box as the PLAN's <c>{id, name, bbox{min_point, max_point}}</c>.</summary>
    public SpatialUnitResult ToResult() => new(
        Id, Name,
        new Bbox3D(
            new[] { FaceW, FaceS, FaceB },
            new[] { FaceE, FaceN, FaceT }));
}
