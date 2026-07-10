using System.Text.Json;

namespace BimEngine.SpatialLayout;

/// <summary>
/// Cross-process <see cref="ISpatialLayoutSink"/> backed by a shared folder: writes one JSON file
/// per result into a <c>spatial/</c> subfolder of the drop root. The Revit add-in (a separate
/// process) watches that subfolder and builds a mass per unit.
///
/// The dedicated <c>spatial/</c> subfolder keeps these files apart from the other pipeline's
/// <c>GeometryCommand</c> drop files, so the two Revit consumers never read each other's messages.
/// Written via a temp file + atomic rename so a watcher filtering <c>*.json</c> never sees a
/// half-written file.
/// </summary>
public sealed class FileDropSpatialSink : ISpatialLayoutSink
{
    private readonly string _spatialDir;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public FileDropSpatialSink(string dropRoot)
    {
        _spatialDir = Path.Combine(dropRoot, "spatial");
        Directory.CreateDirectory(_spatialDir);
    }

    public async Task SendAsync(SpatialLayoutResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var finalPath = Path.Combine(_spatialDir, $"{result.ProjectId}.json");
        var tmpPath = finalPath + ".tmp";

        var json = JsonSerializer.Serialize(result, _json);
        await File.WriteAllTextAsync(tmpPath, json, cancellationToken);

        File.Move(tmpPath, finalPath, overwrite: true);
    }
}
