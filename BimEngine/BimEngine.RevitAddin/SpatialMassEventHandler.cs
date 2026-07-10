using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimEngine.SpatialLayout;

namespace BimEngine.RevitAddin;

/// <summary>
/// Runs on Revit's MAIN API thread when the spatial ExternalEvent fires. Drains queued
/// <see cref="SpatialLayoutResult"/>s and builds each one inside its own <see cref="Transaction"/>.
/// The spatial-pipeline counterpart of <see cref="GeometryEventHandler"/> — a separate handler so
/// the two pipelines never share a queue or a transaction.
/// </summary>
public sealed class SpatialMassEventHandler : IExternalEventHandler
{
    private readonly ConcurrentQueue<SpatialLayoutResult> _pending = new();
    private readonly SpatialMassBuilder _builder = new();

    /// <summary>Called from the background watcher thread — thread-safe enqueue only.</summary>
    public void Enqueue(SpatialLayoutResult result) => _pending.Enqueue(result);

    public void Execute(UIApplication app)
    {
        var uiDoc = app.ActiveUIDocument;
        if (uiDoc is null)
        {
            TaskDialog.Show("BimEngine", "No active document — open a project, then re-send the spatial layout.");
            return;
        }

        var doc = uiDoc.Document;
        while (_pending.TryDequeue(out var result))
        {
            using var t = new Transaction(doc, $"BimEngine: Spatial masses {result.ProjectId}");
            t.Start();
            try
            {
                _builder.Build(doc, result);
                t.Commit();
            }
            catch (Exception ex)
            {
                t.RollBack();
                TaskDialog.Show("BimEngine", $"Failed to build spatial layout {result.ProjectId}:\n{ex.Message}");
            }
        }
    }

    public string GetName() => "BimEngine Spatial Mass Handler";
}
