using System.Text.Json;
using Autodesk.Revit.UI;
using BimEngine.SpatialLayout;

namespace BimEngine.RevitAddin;

/// <summary>
/// Watches <c>{drop}/spatial</c> for <see cref="SpatialLayoutResult"/> JSON files (written by
/// <c>FileDropSpatialSink</c>), deserializes each, and marshals it onto Revit's main thread via the
/// spatial <see cref="ExternalEvent"/>. Runs on a background thread and touches NO Revit API itself
/// — it only enqueues + raises, mirroring the consume side of <c>FileDropMessageQueue</c>.
///
/// The dedicated <c>spatial/</c> subfolder is what keeps this consumer and the <c>GeometryCommand</c>
/// consumer from reading each other's files.
/// </summary>
public sealed class SpatialLayoutWatcher : IDisposable
{
    private readonly string _spatialDir;
    private readonly string _processedDir;
    private readonly SpatialMassEventHandler _handler;
    private readonly ExternalEvent _externalEvent;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private FileSystemWatcher? _watcher;

    public SpatialLayoutWatcher(string dropRoot, SpatialMassEventHandler handler, ExternalEvent externalEvent)
    {
        _spatialDir = Path.Combine(dropRoot, "spatial");
        _processedDir = Path.Combine(_spatialDir, "processed");
        Directory.CreateDirectory(_spatialDir);
        Directory.CreateDirectory(_processedDir);
        _handler = handler;
        _externalEvent = externalEvent;
    }

    public void Start()
    {
        _watcher = new FileSystemWatcher(_spatialDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, e) => TryProcess(e.FullPath);
        _watcher.Renamed += (_, e) => TryProcess(e.FullPath);

        // Sweep files already present before the watcher started.
        foreach (var path in Directory.EnumerateFiles(_spatialDir, "*.json"))
            TryProcess(path);
    }

    private void TryProcess(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;
        if (!TryRead(path, out var result) || result is null) return;

        // Move first so a duplicate watcher event for the same file is a no-op.
        var dest = Path.Combine(_processedDir, Path.GetFileName(path));
        try { File.Move(path, dest, overwrite: true); }
        catch (IOException) { return; } // already claimed by another event

        _handler.Enqueue(result);
        _externalEvent.Raise();
    }

    private bool TryRead(string path, out SpatialLayoutResult? result)
    {
        result = null;
        // The atomic move on the writer means the file is complete, but retry briefly in case the OS
        // is still releasing the handle right after the rename.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var json = File.ReadAllText(path);
                result = JsonSerializer.Deserialize<SpatialLayoutResult>(json, _json);
                return result is not null;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (JsonException)
            {
                return false; // malformed → skip, don't spin
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }
}
