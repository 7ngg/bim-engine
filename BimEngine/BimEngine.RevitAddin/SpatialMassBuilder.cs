using Autodesk.Revit.DB;
using BimEngine.SpatialLayout;

namespace BimEngine.RevitAddin;

/// <summary>
/// Translates a <see cref="SpatialLayoutResult"/> (the PLAN pipeline) into real Revit geometry: one
/// <see cref="DirectShape"/> box (category <c>OST_GenericModel</c>) per spatial unit, extruded from
/// its 3D bounding box. This is the Revit realisation of the article's Stage 3 ("3D kütlə") — Generic
/// Model rather than Mass so the result is visible without toggling "Show Mass".
///
/// Independent of <see cref="RevitGeometryBuilder"/>: it consumes the spatial model directly, so no
/// bbox→footprint conversion ever happens. Runs on Revit's main API thread inside an already-open
/// Transaction (see <see cref="SpatialMassEventHandler"/>). All coordinates arrive in metres and are
/// converted to Revit's internal feet.
/// </summary>
public sealed class SpatialMassBuilder
{
    // Stable tag on every element we create, so a later send can find + delete the previous masses
    // (clean replace). Distinct from the other pipeline's "BimEngine" marker so the two never collide.
    private const string Marker = "BimEngineSpatial";

    private static double ToFeet(double metres) =>
        UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

    public void Build(Document doc, SpatialLayoutResult result)
    {
        // Clean replace: drop spatial masses from a previous send so re-posting a layout replaces
        // the model instead of stacking a copy on top.
        ClearPrevious(doc);

        // Generic Model (NOT OST_Mass): masses live under the Mass category, which Revit hides unless
        // "Show Mass" is toggled on — so a correct build looks like nothing happened. Generic Model is
        // visible in every view by default, which is what a PoC wants.
        var shapeCategory = new ElementId(BuiltInCategory.OST_GenericModel);
        foreach (var unit in result.Units)
        {
            var solid = TryCreateBox(unit.Bbox);
            if (solid is null) continue;

            var directShape = DirectShape.CreateElement(doc, shapeCategory);
            directShape.SetShape(new List<GeometryObject> { solid });
            TrySetName(() => directShape.Name = unit.Name);
            Tag(directShape);
        }
    }

    // Build a rectangular box solid from an axis-aligned bbox: a horizontal rectangle at min z,
    // extruded up by (max z - min z). Returns null for a degenerate/invalid box.
    private static Solid? TryCreateBox(Bbox3D bbox)
    {
        var min = bbox.MinPoint;
        var max = bbox.MaxPoint;
        if (min is null || max is null || min.Length < 3 || max.Length < 3) return null;

        double x0 = ToFeet(min[0]), y0 = ToFeet(min[1]), z0 = ToFeet(min[2]);
        double x1 = ToFeet(max[0]), y1 = ToFeet(max[1]), z1 = ToFeet(max[2]);
        var height = z1 - z0;
        if (height <= 0 || x1 <= x0 || y1 <= y0) return null;

        var p0 = new XYZ(x0, y0, z0);
        var p1 = new XYZ(x1, y0, z0);
        var p2 = new XYZ(x1, y1, z0);
        var p3 = new XYZ(x0, y1, z0);

        var loop = CurveLoop.Create(new List<Curve>
        {
            Line.CreateBound(p0, p1),
            Line.CreateBound(p1, p2),
            Line.CreateBound(p2, p3),
            Line.CreateBound(p3, p0),
        });

        return GeometryCreationUtilities.CreateExtrusionGeometry(
            new List<CurveLoop> { loop }, XYZ.BasisZ, height);
    }

    // Stamp an element as spatial-BimEngine (Comments param) so ClearPrevious can find it later.
    private static void Tag(Element element) =>
        element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Marker);

    // Delete masses from a previous spatial send. Only Marker-tagged elements match, so pre-existing
    // model content — and the other pipeline's "BimEngine" geometry — is left untouched.
    private static void ClearPrevious(Document doc)
    {
        var stale = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .Where(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == Marker)
            .Select(e => e.Id)
            .ToList();

        if (stale.Count > 0) doc.Delete(stale);
        doc.Regenerate();
    }

    // Naming a DirectShape is cosmetic; a clash is non-fatal for a PoC.
    private static void TrySetName(Action set)
    {
        try { set(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { /* duplicate/invalid name — leave default */ }
    }
}
