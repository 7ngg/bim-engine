using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BimEngine.RevitAddin;

/// <summary>
/// Ribbon button handler: shows the add-in status and which folder it is watching. Read-only, so
/// it needs no document modification (Transaction mode Manual, but no transaction opened).
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class StatusCommand : IExternalCommand
{
    public static string DropFolder { get; set; } = "(not started)";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        TaskDialog.Show(
            "BimEngine",
            "BimEngine consumer is running.\n\n" +
            $"Watching drop folder:\n{DropFolder}\n\n" +
            "POST a building request to the BimEngine API (Transport=FileDrop) and the geometry " +
            "will appear in the active document.");
        return Result.Succeeded;
    }
}
