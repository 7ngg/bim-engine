namespace BimEngine.SpatialLayout;

/// <summary>
/// The relation-matrix codes from the PLAN. A <c>matrix[a][b]</c> entry states how spatial unit
/// <c>a</c> relates to unit <c>b</c> (rows/cols align with the <c>spatial_units</c> order).
///
/// Codes 3 and 4 are inverse containment directions: the sample matrix is symmetric everywhere
/// except <c>[0][3]=3</c> (Entrance contains Platform) vs <c>[3][0]=4</c> (Platform contained by
/// Entrance), which is what pins the meaning of each code.
/// </summary>
public static class RelationCode
{
    /// <summary>No direct relation (also the diagonal / self).</summary>
    public const int None = 0;

    /// <summary>Share a wall — placed edge-to-edge.</summary>
    public const int Adjacency = 1;

    /// <summary>Overlap by the minimum passage (1 m threshold).</summary>
    public const int Intersection = 2;

    /// <summary><c>a</c> contains <c>b</c> — <c>b</c> nested inside <c>a</c>.</summary>
    public const int Contains = 3;

    /// <summary><c>a</c> is contained by <c>b</c> — i.e. <c>b</c> encloses <c>a</c> (inverse of <see cref="Contains"/>).</summary>
    public const int ContainedBy = 4;

    /// <summary>Highest code value, used by validation.</summary>
    public const int Max = ContainedBy;
}
