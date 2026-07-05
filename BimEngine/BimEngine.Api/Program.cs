using BimEngine.Api.Services;
using BimEngine.Core.Contracts;
using BimEngine.Infrastructure;
using BimEngine.MockConsumer;

var builder = WebApplication.CreateBuilder(args);

// --- Composition root --------------------------------------------------------------------------
// The queue is a SINGLETON so the API (producer) and the background consumer share one channel.
builder.Services.AddSingleton<IMessageQueue, InMemoryMessageQueue>();

// RAG-style validation/enrichment. Stateless -> singleton is fine.
builder.Services.AddSingleton<IRagService, RagService>();

// Mock Revit consumer: hosted in THIS process as a BackgroundService for the PoC. It resolves the
// same IMessageQueue singleton registered above. Swap this line for a real out-of-process Revit
// add-in later without touching the API. (Registered so the same instance backs the hosted service.)
builder.Services.AddSingleton<MockRevitConsumer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MockRevitConsumer>());

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Swagger UI always on — this is a demo/PoC.
app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

app.Run();
