using BimEngine.Api.Services;
using BimEngine.Core.Contracts;
using BimEngine.Infrastructure;
using BimEngine.MockConsumer;
using BimEngine.SpatialLayout;

var builder = WebApplication.CreateBuilder(args);

// --- Composition root --------------------------------------------------------------------------
// RAG-style validation/enrichment: user-authored FloorPlanBrief -> GeometryCommand. Stateless ->
// singleton is fine.
builder.Services.AddSingleton<IRagService, RagService>();

// Second, INDEPENDENT pipeline (the PLAN): relation-matrix SpatialLayoutRequest -> 3D masses. Its
// own engine + sink; shares nothing with the RAG/GeometryCommand path above. Stateless -> singleton.
builder.Services.AddSingleton<SpatialLayoutEngine>();

// Transport is switchable (DI-only — the IMessageQueue seam never leaks to callers):
//   "InMemory" (default): single-process demo. Channel<T> queue + in-process MockRevitConsumer.
//                         Zero setup — one `dotnet run` runs the whole pipeline.
//   "FileDrop":           cross-process. API writes command files to a shared folder that a real
//                         Revit add-in (separate process) watches. No mock consumer here.
var transport = builder.Configuration["Transport"] ?? "InMemory";

if (string.Equals(transport, "FileDrop", StringComparison.OrdinalIgnoreCase))
{
    var configuredDrop = builder.Configuration["DropFolder"];
    var dropDir = string.IsNullOrWhiteSpace(configuredDrop)
        ? Path.Combine(Path.GetTempPath(), "BimEngine", "drop")
        : configuredDrop;
    // Singleton so the whole app shares one instance over the shared folder.
    builder.Services.AddSingleton<IMessageQueue>(_ => new FileDropMessageQueue(dropDir));

    // Spatial pipeline uses the same drop root (its own `spatial/` subfolder) so the Revit mass
    // consumer picks results up cross-process.
    builder.Services.AddSingleton<ISpatialLayoutSink>(_ => new FileDropSpatialSink(dropDir));
}
else
{
    // The queue is a SINGLETON so the API (producer) and the consumer share one channel.
    builder.Services.AddSingleton<IMessageQueue, InMemoryMessageQueue>();

    // Mock Revit consumer, hosted in THIS process as a BackgroundService. Resolves the same
    // IMessageQueue singleton. The real Revit add-in replaces this out-of-process (FileDrop).
    builder.Services.AddSingleton<MockRevitConsumer>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MockRevitConsumer>());

    // Spatial pipeline's zero-setup consumer: log the masses it would build.
    builder.Services.AddSingleton<ISpatialLayoutSink, LoggingSpatialSink>();
}

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Swagger UI always on — this is a demo/PoC.
app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

app.Run();
