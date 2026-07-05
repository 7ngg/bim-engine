using System.Text.Json;
using System.Threading.Channels;
using BimEngine.Core.Contracts;
using BimEngine.Core.Models;

namespace BimEngine.Infrastructure;

/// <summary>
/// Cross-process <see cref="IMessageQueue"/> backed by a shared folder. The API (one process)
/// publishes a JSON file per command; the Revit add-in (another process) watches the folder and
/// consumes them. Lets Revit — which must run in its own single-threaded process — receive work
/// without any network broker.
///
/// Same <see cref="IMessageQueue"/> contract as <see cref="InMemoryMessageQueue"/>, so swapping to
/// this is a DI-only change. A future RabbitMqMessageQueue would swap in the same way.
/// </summary>
public sealed class FileDropMessageQueue : IMessageQueue
{
    private readonly string _dropDir;
    private readonly string _processedDir;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public FileDropMessageQueue(string dropDir)
    {
        _dropDir = dropDir;
        _processedDir = Path.Combine(dropDir, "processed");
        Directory.CreateDirectory(_dropDir);
        Directory.CreateDirectory(_processedDir);
    }

    public async Task PublishAsync(GeometryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var finalPath = Path.Combine(_dropDir, $"{command.ProjectId}.json");
        var tmpPath = finalPath + ".tmp";

        var json = JsonSerializer.Serialize(command, _json);
        await File.WriteAllTextAsync(tmpPath, json, cancellationToken);

        // Atomic rename so a watcher filtering *.json never sees a half-written file.
        File.Move(tmpPath, finalPath, overwrite: true);
    }

    public async IAsyncEnumerable<GeometryCommand> ConsumeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<GeometryCommand>(
            new UnboundedChannelOptions { SingleReader = true });

        void TryProcess(string path)
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;

            if (TryReadCommand(path, out var command))
            {
                // Move first so a duplicate watcher event for the same file is a no-op.
                var dest = Path.Combine(_processedDir, Path.GetFileName(path));
                try { File.Move(path, dest, overwrite: true); }
                catch (IOException) { return; } // already claimed by another event

                channel.Writer.TryWrite(command!);
            }
        }

        using var watcher = new FileSystemWatcher(_dropDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, e) => TryProcess(e.FullPath);
        watcher.Renamed += (_, e) => TryProcess(e.FullPath);

        // Sweep files already present before the watcher started.
        foreach (var path in Directory.EnumerateFiles(_dropDir, "*.json"))
            TryProcess(path);

        await foreach (var command in channel.Reader.ReadAllAsync(cancellationToken))
            yield return command;
    }

    private bool TryReadCommand(string path, out GeometryCommand? command)
    {
        command = null;
        // The atomic move means the file is complete, but retry briefly in case the OS is still
        // releasing the handle right after the rename.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var json = File.ReadAllText(path);
                command = JsonSerializer.Deserialize<GeometryCommand>(json, _json);
                return command is not null;
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
}
