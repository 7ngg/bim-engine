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

For the real Revit path the consumer moves to its own process (Revit); the API hands off via a shared
drop folder instead of the in-process channel — same `IMessageQueue` contract:

```
client ─POST─▶ API (RAG validate + enrich + LAYOUT) ─▶ GeometryCommand JSON
                                                        │ FileDropMessageQueue.Publish (writes a file)
                                                        ▼
                                              shared drop folder
                                                        │ FileSystemWatcher   (Revit process)
                                                        ▼
                        BimEngine.RevitAddin ─▶ ExternalEvent ─▶ builds Levels/Walls/Rooms/Doors
```

## Projects

| Project | What it is |
|---|---|
| `BimEngine.Core` | Models (`BuildingRequest`, `RoomSpec` + `RoomFootprint`, `GeometryCommand` + `DoorSpec`) + contracts (`IMessageQueue`, `IGeometryConsumer`). No dependencies. |
| `BimEngine.Infrastructure` | `InMemoryMessageQueue` (in-process `Channel<T>`) **and** `FileDropMessageQueue` (cross-process shared folder). Both implement the same `IMessageQueue`. |
| `BimEngine.Api` | ASP.NET Core Web API. `POST /projects`, `RagService` (norm validation + room **layout** + door derivation), Swagger. Hosts the mock consumer in `InMemory` mode. |
| `BimEngine.MockConsumer` | `MockRevitConsumer` — subscribes to the queue and logs each room. The zero-setup, no-Revit demo consumer. |
| `BimEngine.SpatialLayout` | **Independent** relation-matrix pipeline (the PLAN): `SpatialLayoutEngine` packs `spatial_units` + `relation_matrix` into 3D masses. Own request/result types + `ISpatialLayoutSink` (`FileDropSpatialSink`). Dependency-free. See [Spatial-layout pipeline](#spatial-layout-pipeline-relation-matrix--a-second-independent-producer). |
| `BimEngine.RevitAddin` | **Real Revit add-in** (Windows + Revit 2025/2026 only). Consumes `GeometryCommand`s from the drop folder and builds Levels/Walls/Rooms/Doors. Not part of `BimEngine.sln` — see [Revit add-in](#revit-add-in-windows--revit-20252026). |

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

Returns **202 Accepted** with the generated `GeometryCommand` JSON so you can see what was produced.
Each room now carries a concrete `footprint` (metres, floor-local) and the command carries the
derived `doors` — everything the Revit add-in needs to draw the schema:

```json
{
  "projectId": "PRJ-20260705-113236-3703a9",
  "floorCount": 2,
  "floorHeightM": 3,
  "rooms": [
    { "name": "Living Room", "areaSqm": 14, "floorIndex": 0, "adjacentTo": ["Hallway (Floor 0)"],
      "footprint": { "originXm": 0, "originYm": 2, "widthM": 3.5, "depthM": 4 } }
    // ... bedrooms, bathrooms, one full-width hallway per floor
  ],
  "doors": [
    { "fromRoom": "Living Room", "toRoom": "Hallway (Floor 0)", "centerXm": 1.75, "centerYm": 2, "floorIndex": 0 }
    // ... one per shared wall
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

## Spatial-layout pipeline (relation matrix) — a second, independent producer

Alongside the `FloorPlanBrief` flow above, `BimEngine.SpatialLayout` implements a **completely separate**
generator: instead of a rich brief, an LLM emits a compact **relation matrix** and the engine packs the spaces
into **3D bounding boxes** (the article's `SpatialUnit` / `LayoutGenerator`, generalized to any number of units).
It shares nothing with `GeometryCommand`/`RoomSpec` — its own request and result types travel end-to-end, so no
data is converted or lost — but it still reaches Revit through its own drop channel and its own mass consumer:

```
client ─POST /spatial-layout─▶ SpatialLayoutEngine ─▶ SpatialLayoutResult (id,name,bbox{min_point,max_point})
                                                          │ ISpatialLayoutSink
                                          ┌───────────────┴───────────────┐
                              InMemory: LoggingSpatialSink      FileDrop: FileDropSpatialSink → {drop}/spatial/*.json
                                   (logs the masses)                        │ SpatialLayoutWatcher (Revit process)
                                                                            ▼
                                                          SpatialMassBuilder → one DirectShape mass per unit
```

**Input** is the PLAN JSON verbatim. Each unit's `area`·`ratio` give its width/length; `height`/`levelHeight`
give its z extent. The `relation_matrix` (`matrix[a][b]`, rows/cols aligned with `spatial_units`) drives placement:

| code | meaning | placement |
|---|---|---|
| 0 | none / self | — |
| 1 | adjacency | edge-to-edge (shared wall) |
| 2 | intersection | overlap by the 1 m minimum passage |
| 3 | `a` contains `b` | `b` nested inside `a` |
| 4 | `a` contained by `b` | `b` encloses `a` (inverse of 3) |

```bash
curl -X POST http://localhost:5080/spatial-layout \
  -H 'Content-Type: application/json' \
  -d '{
    "spatial_units": [
      {"id": 0, "name": "Entrance",  "area": 9.0, "ratio": 1.0, "height": 3.0, "levelHeight": 0.0},
      {"id": 1, "name": "RestSpace", "area": 9.0, "ratio": 1.0, "height": 3.0, "levelHeight": 0.0},
      {"id": 2, "name": "Dining",    "area": 9.0, "ratio": 1.0, "height": 3.0, "levelHeight": 1.0},
      {"id": 3, "name": "Platform",  "area": 1.0, "ratio": 1.0, "height": 2.0, "levelHeight": 1.0}
    ],
    "relation_matrix": [[0,1,1,3],[1,0,2,1],[1,2,0,1],[4,1,1,0]]
  }'
```

Returns **202** with the packed boxes (the exact PLAN output shape):

```json
{
  "projectId": "SPL-20260710-125515-06a42b",
  "units": [
    { "id": 0, "name": "Entrance",  "bbox": { "min_point": [0,0,0],       "max_point": [3,3,3] } },
    { "id": 1, "name": "RestSpace", "bbox": { "min_point": [3,0,0],       "max_point": [6,3,3] } },
    { "id": 2, "name": "Dining",    "bbox": { "min_point": [5,0,1],       "max_point": [8,3,4] } },
    { "id": 3, "name": "Platform",  "bbox": { "min_point": [0.5,0.5,1],   "max_point": [1.5,1.5,3] } }
  ]
}
```

Same `Transport` switch as the other pipeline: `InMemory` logs the masses (`[MOCK REVIT/SPATIAL] …`); `FileDrop`
writes `{DropFolder}/spatial/SPL-*.json`, which the add-in's `SpatialLayoutWatcher` + `SpatialMassBuilder` turn
into DirectShape masses in the active Revit document (independent of the Levels/Walls/Rooms path). A malformed
request (non-square matrix, non-positive area/ratio/height, duplicate id) is rejected with **400** before anything
is published.

## Transport modes

Selected by config (`appsettings.json` or env var `Transport`) — the `IMessageQueue` swap is DI-only,
callers never change:

| `Transport` | Queue | Consumer | Setup |
|---|---|---|---|
| `InMemory` (default) | `InMemoryMessageQueue` (`Channel<T>`) | `MockRevitConsumer` in-process | none — one `dotnet run` |
| `FileDrop` | `FileDropMessageQueue` (shared folder) | the Revit add-in, separate process | shared folder (defaults to `%TEMP%/BimEngine/drop`) |

```bash
# FileDrop mode (API only publishes; Revit consumes)
Transport=FileDrop DropFolder=/path/to/drop dotnet run --project BimEngine.Api
```

## Revit add-in (Windows + Revit 2025/2026)

`BimEngine.RevitAddin` is the **real** consumer. It is deliberately **not** in `BimEngine.sln`
(so `dotnet build` stays green on Linux/CI); build it explicitly on a Windows box that has Revit:

```powershell
# from the BimEngine folder, on Windows with Revit 2025 installed
dotnet build BimEngine.RevitAddin/BimEngine.RevitAddin.csproj -p:RevitVersion=2025
```

**One-shot launcher:** `run-windows.ps1` does the whole thing — sets the shared drop folder,
builds + deploys the add-in, starts Revit, then runs the API in FileDrop mode:

```powershell
.\run-windows.ps1                                 # Revit 2025, drop C:\BimEngineDrop
.\run-windows.ps1 -RevitVersion 2026 -DropFolder D:\drop
.\run-windows.ps1 -SkipAddinBuild -SkipRevit      # API only
```

Manual steps below if you'd rather run each process yourself.

The build's `DeployAddin` target copies the DLLs + `BimEngine.RevitAddin.addin` into
`%AppData%\Autodesk\Revit\Addins\2025\`. Then:

1. Start the API in FileDrop mode. Point it at a folder the add-in also watches — either set
   `DropFolder` on the API and the `BIMENGINE_DROP` env var for Revit to the same path, or leave
   both unset to use the shared default `%TEMP%/BimEngine/drop`.
2. Launch Revit and open/create a project from the **Architectural** template (guarantees a door
   family + a phase). A **BimEngine** ribbon panel appears; its button shows the watched folder.
3. `POST /projects`. The add-in picks up the command file and builds **Levels, Walls, Rooms, and
   Doors** in the active document, matching the JSON.

How it satisfies Revit's threading rule: a background loop reads the drop folder and only
*enqueues* each command + calls `ExternalEvent.Raise()`; Revit then runs `GeometryEventHandler`
on its **main API thread**, inside a `Transaction`, where `RevitGeometryBuilder` calls the Revit
API. The API is single-threaded, so geometry can never be built on the consumer thread directly.

## Where the real integrations plug in (the seams)

The PoC is built around two interfaces so real implementations drop in without touching callers:

### 1. `IRagService` → real RAG / norm lookup
`BimEngine.Api/Services/RagService.cs` uses **hardcoded** norm constants. See the
`TODO(RAG)` comment: replace the constants with retrieval over indexed building-code
PDFs/Excel (embed the request → retrieve relevant clauses from a vector store → let an
LLM extract the concrete numbers). The method still returns a `GeometryCommand`; the
controller never changes.

### 2. `IGeometryConsumer` → real Revit add-in ✅ implemented
`MockRevitConsumer` (logs) and `BimEngine.RevitAddin.RevitGeometryConsumer` (drives Revit) both
implement the same `IGeometryConsumer`. The Revit one runs **inside Revit's process** (loaded via
`BimEngine.RevitAddin.addin`) and marshals each `GeometryCommand` onto Revit's main thread via
`IExternalEventHandler` + `ExternalEvent.Raise()` before calling the Revit API. See
[Revit add-in](#revit-add-in-windows--revit-20252026). To grow it, extend `RevitGeometryBuilder`
(e.g. windows, floors/roofs, real families).

### 3. `IMessageQueue` → cross-process transport ✅ file-drop implemented
`InMemoryMessageQueue` (one process) and `FileDropMessageQueue` (shared folder, two processes) both
implement `IMessageQueue`; the choice is DI-only (`Transport` config). A future
`RabbitMqMessageQueue` (or Azure Service Bus / Kafka) drops in the same way for networked/scalable
delivery, with no change to the API, `RagService`, or the add-in.
