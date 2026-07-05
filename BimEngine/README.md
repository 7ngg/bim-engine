# BimEngine — End-to-End Pipeline PoC

Proof-of-concept data pipeline for a future Autodesk Revit integration:

```
client ──POST──▶ API (RAG validate + enrich) ──▶ GeometryCommand JSON
                                                        │ publish
                                                        ▼
                                                  IMessageQueue
                                                        │ consume
                                                        ▼
                              MockRevitConsumer ──▶ logs what it WOULD build
```

The whole flow runs in **one process** with a single `dotnet run` — no broker, no Revit, no extra setup.

## Projects

| Project | What it is |
|---|---|
| `BimEngine.Core` | Models (`BuildingRequest`, `RoomSpec`, `GeometryCommand`) + contracts (`IMessageQueue`, `IGeometryConsumer`). No dependencies. |
| `BimEngine.Infrastructure` | `InMemoryMessageQueue` — pub/sub over `System.Threading.Channels.Channel<T>`. Registered as a **singleton** so producer and consumer share one channel. |
| `BimEngine.Api` | ASP.NET Core Web API. `POST /projects`, `RagService` (norm validation + room distribution), Swagger. Also hosts the consumer as a `BackgroundService` for the PoC. |
| `BimEngine.MockConsumer` | `MockRevitConsumer` — subscribes to the queue and logs each room. Stands in for the real Revit add-in. |

## Requirements

- .NET 8 SDK (targets `net8.0`).
- Only a newer runtime installed? Run with roll-forward:
  `DOTNET_ROLL_FORWARD=LatestMajor dotnet run --project BimEngine.Api`

## Run

```bash
cd BimEngine
dotnet run --project BimEngine.Api
```

Swagger UI: **http://localhost:5080/swagger**

## Test

### curl

```bash
curl -X POST http://localhost:5080/projects \
  -H 'Content-Type: application/json' \
  -d '{"floorCount":2,"bedrooms":3,"bathrooms":2,"plotAreaSqm":120,"buildingType":"house"}'
```

Returns **202 Accepted** with the generated `GeometryCommand` JSON so you can see what was produced:

```json
{
  "projectId": "PRJ-20260705-105222-1cc427",
  "floorCount": 2,
  "rooms": [
    { "name": "Hallway (Floor 0)", "areaSqm": 4, "floorIndex": 0, "adjacentTo": [] },
    { "name": "Living Room", "areaSqm": 14, "floorIndex": 0, "adjacentTo": ["Hallway (Floor 0)"] },
    { "name": "Bedroom 1", "areaSqm": 9, "floorIndex": 0, "adjacentTo": ["Hallway (Floor 0)"] }
    // ...
  ]
}
```

### Norm validation (expect 400)

Requests that violate a hardcoded building norm are rejected before anything is queued:

```bash
curl -X POST http://localhost:5080/projects \
  -H 'Content-Type: application/json' \
  -d '{"floorCount":1,"bedrooms":5,"bathrooms":1,"plotAreaSqm":120,"buildingType":"house"}'
# 400: "5 bedrooms require at least 2 bathroom(s) (1 per 3 bedrooms); got 1."
```

Rules today (all hardcoded in `RagService`): min bedroom/bathroom/kitchen/living areas, bathroom-to-bedroom ratio, footprint-fits-program sanity, assumed 3 m floor height.

### Or use Swagger UI

Open `/swagger`, expand `POST /projects`, **Try it out**, send the body.

## Expected console output

After a valid POST the consumer prints one line per room:

```
[MOCK REVIT] Consumer started. Waiting for geometry commands...
[MOCK REVIT] Received project PRJ-20260705-105222-1cc427: 2 floor(s), 9 room(s)
[MOCK REVIT] Would create Room 'Hallway (Floor 0)' (4 sqm) on Floor 0, adjacent to: (none)
[MOCK REVIT] Would create Room 'Living Room' (14 sqm) on Floor 0, adjacent to: Hallway (Floor 0)
[MOCK REVIT] Would create Room 'Bedroom 1' (9 sqm) on Floor 0, adjacent to: Hallway (Floor 0)
...
```

That proves the full path: client → API/RAG → queue → consumer.

## Where the real integrations plug in (the seams)

The PoC is built around two interfaces so real implementations drop in without touching callers:

### 1. `IRagService` → real RAG / norm lookup
`BimEngine.Api/Services/RagService.cs` uses **hardcoded** norm constants. See the
`TODO(RAG)` comment: replace the constants with retrieval over indexed building-code
PDFs/Excel (embed the request → retrieve relevant clauses from a vector store → let an
LLM extract the concrete numbers). The method still returns a `GeometryCommand`; the
controller never changes.

### 2. `IGeometryConsumer` → real Revit add-in
`MockRevitConsumer` implements `IGeometryConsumer` and just logs. A production consumer
runs **inside Revit's process** (loaded via a `.addin` manifest), exposes an
`IExternalCommand` entry point, and marshals each `GeometryCommand` onto Revit's main
thread via `IExternalEventHandler` + `ExternalEvent.Raise()` before calling the Revit API
(`Document.Create.*`). The Revit API is single-threaded, so it **cannot** run on the queue
consumer thread directly. Only the body of `ProcessAsync` changes.

### 3. `IMessageQueue` → real broker
`InMemoryMessageQueue` can be swapped for a `RabbitMqMessageQueue` (or Azure Service Bus /
Kafka) implementing the same interface. Only the DI registration in `Program.cs` changes —
once Revit is a separate process, the in-memory channel is replaced by a network broker and
producer/consumer no longer need to share a process.
