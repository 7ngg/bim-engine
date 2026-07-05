using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using BimEngine.Core.Models;

namespace BimEngine.RevitAddin;

/// <summary>
/// Translates a <see cref="GeometryCommand"/> into real Revit elements: a Level per floor, four
/// walls per room footprint, a Room in each enclosed rectangle, and a door on each shared wall.
///
/// Runs entirely on Revit's main API thread inside an already-open Transaction
/// (see <see cref="GeometryEventHandler"/>). All model coordinates arrive in metres and are
/// converted to Revit's internal feet.
///
/// Kept intentionally simple/readable — this is a PoC renderer, not a production layout engine.
/// </summary>
public sealed class RevitGeometryBuilder
{
    private static double ToFeet(double metres) =>
        UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

    public void Build(Document doc, GeometryCommand command)
    {
        var wallType = FirstBasicWallType(doc);
        var heightFt = ToFeet(command.FloorHeightM);

        // 1. One Level per floor.
        var levels = new Dictionary<int, Level>();
        for (var floor = 0; floor < command.FloorCount; floor++)
        {
            var level = Level.Create(doc, ToFeet(floor * command.FloorHeightM));
            TrySetName(() => level.Name = $"BimEngine {command.ProjectId} F{floor}");
            levels[floor] = level;
        }

        // 2. Walls per room footprint. Track walls per floor so doors can find a host.
        var wallsByFloor = new Dictionary<int, List<Wall>>();
        foreach (var room in command.Rooms)
        {
            if (room.Footprint is null || !levels.TryGetValue(room.FloorIndex, out var level))
                continue;

            var walls = wallsByFloor.TryGetValue(room.FloorIndex, out var existing)
                ? existing
                : wallsByFloor[room.FloorIndex] = new List<Wall>();

            walls.AddRange(BuildRoomWalls(doc, room.Footprint, wallType, level, heightFt));
        }

        // Rooms and doors both need the newly created walls to be part of the model graph.
        doc.Regenerate();

        // 3. A Room inside each footprint.
        foreach (var room in command.Rooms)
        {
            if (room.Footprint is null || !levels.TryGetValue(room.FloorIndex, out var level))
                continue;

            var fp = room.Footprint;
            var centre = new UV(ToFeet(fp.OriginXm + fp.WidthM / 2), ToFeet(fp.OriginYm + fp.DepthM / 2));
            var placed = doc.Create.NewRoom(level, centre);
            if (placed is not null)
                TrySetName(() => placed.Name = room.Name);
        }

        // 4. A door on each shared wall.
        var doors = command.Doors;
        if (doors is { Count: > 0 })
        {
            var doorSymbol = FirstDoorSymbol(doc);
            if (doorSymbol is not null)
            {
                if (!doorSymbol.IsActive) doorSymbol.Activate();
                doc.Regenerate();

                foreach (var door in doors)
                {
                    if (!levels.TryGetValue(door.FloorIndex, out var level)) continue;
                    if (!wallsByFloor.TryGetValue(door.FloorIndex, out var walls)) continue;

                    var point = new XYZ(ToFeet(door.CenterXm), ToFeet(door.CenterYm), level.Elevation);
                    var host = NearestWall(walls, point);
                    if (host is null) continue;

                    doc.Create.NewFamilyInstance(point, doorSymbol, host, level, StructuralType.NonStructural);
                }
            }
        }
    }

    private static IEnumerable<Wall> BuildRoomWalls(
        Document doc, RoomFootprint fp, WallType wallType, Level level, double heightFt)
    {
        var z = level.Elevation;
        var x0 = ToFeet(fp.OriginXm);
        var y0 = ToFeet(fp.OriginYm);
        var x1 = ToFeet(fp.OriginXm + fp.WidthM);
        var y1 = ToFeet(fp.OriginYm + fp.DepthM);

        var c0 = new XYZ(x0, y0, z);
        var c1 = new XYZ(x1, y0, z);
        var c2 = new XYZ(x1, y1, z);
        var c3 = new XYZ(x0, y1, z);

        foreach (var (a, b) in new[] { (c0, c1), (c1, c2), (c2, c3), (c3, c0) })
        {
            var wall = Wall.Create(doc, Line.CreateBound(a, b), wallType.Id, level.Id,
                heightFt, offset: 0, flip: false, structural: false);
            yield return wall;
        }
    }

    private static Wall? NearestWall(List<Wall> walls, XYZ point)
    {
        Wall? best = null;
        var bestDist = double.MaxValue;
        foreach (var wall in walls)
        {
            if (wall.Location is not LocationCurve lc) continue;
            var dist = lc.Curve.Distance(point);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = wall;
            }
        }
        // Guard against hosting on a wall that isn't actually at the shared edge (1 ft tolerance).
        return bestDist <= 1.0 ? best : null;
    }

    private static WallType FirstBasicWallType(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(w => w.Kind == WallKind.Basic)
        ?? new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First();

    private static FamilySymbol? FirstDoorSymbol(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_Doors)
            .Cast<FamilySymbol>()
            .FirstOrDefault();

    // Names can clash with existing elements; a duplicate name is non-fatal for a PoC.
    private static void TrySetName(Action set)
    {
        try { set(); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* duplicate name — leave default */ }
    }
}
