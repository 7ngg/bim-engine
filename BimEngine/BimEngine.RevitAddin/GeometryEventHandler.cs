using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimEngine.Core.Models;

namespace BimEngine.RevitAddin;

/// <summary>
/// Runs on Revit's MAIN API thread when the ExternalEvent fires. Drains queued commands and builds
/// each one inside its own <see cref="Transaction"/>. This is the only place the Revit API is
/// legally touched.
/// </summary>
public sealed class GeometryEventHandler : IExternalEventHandler
{
    private readonly ConcurrentQueue<GeometryCommand> _pending = new();
    private readonly RevitGeometryBuilder _builder = new();

    /// <summary>Called from the background consumer thread — thread-safe enqueue only.</summary>
    public void Enqueue(GeometryCommand command) => _pending.Enqueue(command);

    public void Execute(UIApplication app)
    {
        var uiDoc = app.ActiveUIDocument;
        if (uiDoc is null)
        {
            TaskDialog.Show("BimEngine", "No active document — open a project, then re-send.");
            return;
        }

        var doc = uiDoc.Document;
        while (_pending.TryDequeue(out var command))
        {
            using var t = new Transaction(doc, $"BimEngine: Build {command.ProjectId}");
            t.Start();
            try
            {
                _builder.Build(doc, command);
                t.Commit();
            }
            catch (Exception ex)
            {
                t.RollBack();
                TaskDialog.Show("BimEngine", $"Failed to build {command.ProjectId}:\n{ex.Message}");
            }
        }
    }

    public string GetName() => "BimEngine Geometry Handler";
}
