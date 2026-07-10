using Autodesk.Revit.UI;
using BimEngine.Core.Contracts;
using BimEngine.Infrastructure;

namespace BimEngine.RevitAddin;

/// <summary>
/// Revit entry point. On startup it wires the marshaling machinery and starts a background loop
/// that pulls <see cref="Core.Models.GeometryCommand"/>s off the shared drop folder.
///
/// Threading model (the crux of Revit integration):
///   * The consumer loop runs on a BACKGROUND thread — it may NOT touch the Revit API.
///   * For each command it calls <see cref="RevitGeometryConsumer.ProcessAsync"/>, which only
///     enqueues the command and calls ExternalEvent.Raise().
///   * Revit later invokes <see cref="GeometryEventHandler.Execute"/> on the MAIN API thread,
///     where the actual geometry is built inside a Transaction.
///
/// NOTE: This file compiles and runs only on Windows with Revit installed (RevitAPI referenced).
/// It cannot be built on the Linux dev box; verify on a Windows + Revit 2025/2026 machine.
/// </summary>
public sealed class App : IExternalApplication
{
    private IMessageQueue? _queue;
    private CancellationTokenSource? _cts;
    private Task? _consumerLoop;

    // Second, INDEPENDENT consumer: the PLAN's spatial-layout masses (own drop subfolder, own event).
    private SpatialLayoutWatcher? _spatialWatcher;

    // Must match the API's DropFolder. Override via the BIMENGINE_DROP env var on either process.
    private static string DropFolder =>
        Environment.GetEnvironmentVariable("BIMENGINE_DROP")
        ?? Path.Combine(Path.GetTempPath(), "BimEngine", "drop");

    public Result OnStartup(UIControlledApplication application)
    {
        // ExternalEvent + handler must be created in a valid Revit API context (OnStartup is one).
        var handler = new GeometryEventHandler();
        var externalEvent = ExternalEvent.Create(handler);
        var consumer = new RevitGeometryConsumer(handler, externalEvent);

        _queue = new FileDropMessageQueue(DropFolder);
        _cts = new CancellationTokenSource();

        // Background pump: queue -> consumer (enqueue + raise). No Revit API calls here.
        _consumerLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var command in _queue.ConsumeAsync(_cts.Token))
                    await consumer.ProcessAsync(command, _cts.Token);
            }
            catch (OperationCanceledException) { /* shutting down */ }
        });

        // Spatial-layout pipeline: its own event handler + folder watcher. The watcher runs on a
        // background thread and only enqueues + raises; SpatialMassEventHandler builds on the main
        // API thread. Fully separate from the GeometryCommand path above.
        var spatialHandler = new SpatialMassEventHandler();
        var spatialEvent = ExternalEvent.Create(spatialHandler);
        _spatialWatcher = new SpatialLayoutWatcher(DropFolder, spatialHandler, spatialEvent);
        _spatialWatcher.Start();

        // A ribbon button so the user can see the add-in is live and where it's watching.
        var panel = application.CreateRibbonPanel("BimEngine");
        var buttonData = new PushButtonData(
            "BimEngineStatus",
            "BimEngine\nStatus",
            typeof(App).Assembly.Location,
            typeof(StatusCommand).FullName);
        panel.AddItem(buttonData);
        StatusCommand.DropFolder = DropFolder;

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _cts?.Cancel();
        try { _consumerLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts?.Dispose();
        _spatialWatcher?.Dispose();
        return Result.Succeeded;
    }
}
