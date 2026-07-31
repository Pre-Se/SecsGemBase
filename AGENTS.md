# AGENTS.md

SecsGemBase is a C# library suite implementing the SECS/GEM protocol for semiconductor equipment. All projects are libraries — there is no runnable application.

## Build

```bash
dotnet restore SecsGemBase.sln
dotnet build SecsGemBase.sln
dotnet build SecsGemBase.sln --configuration Release
```

There are no tests, no linter, and no typecheck in this repo. A successful `dotnet build` is the only verification step.

## Project Dependencies (layered — lower layers must not depend upward)

```
SecsGemMessageHandling   ← top: depends on everything
  ├── SecsGemBaseItems    ← depends on SecsGemHelperClasses
  ├── TCPIPBaseLibrary    ← depends on Logging, SecsGemHelperClasses
  ├── SecsGemHelperClasses ← no project deps (leaf)
  └── Logging             ← depends on SecsGemBaseItems
```

## Target Framework

Every `.csproj` sets `<TargetFramework>net10.0</TargetFramework>` individually, overriding `Directory.Build.props` which says `net9.0`. If you change the target framework, update each `.csproj` — `Directory.Build.props` is not the source of truth.

## Language / Nullability

- `LangVersion` = `preview` (in `Directory.Build.props`)
- `ImplicitUsings` and `Nullable` are enabled in every `.csproj`

## Key NuGet Dependencies

- `FluentResults` (Result pattern — used instead of throwing exceptions)
- `System.Reactive` (observables for incoming message streams)
- `CommunityToolkit.Mvvm` (observable properties for connection state)
- `Microsoft.Extensions.Logging.Abstractions`

## CI Artifacts

- Azure Pipelines (`azure-pipelines.yml`) and GitLab CI (`gitlab-ci.yml`) both trigger on `master` and `development`
- Both build Release, pack NuGet, and publish to internal feeds
- `Logging.csproj` has `GeneratePackageOnBuild=True` (others do not)

## Domain Vocabulary (Naming)

The ReSharper settings register these as single-word abbreviations: `HSMS`, `TCPIP`, `CEID`, `DVID`, `SV`, `EC`, `GEM`, `SECS`. Always write `HSMSState`, `TCPIPClient`, etc. — never `HsmsState`.

## Thread Safety in CommunicationHandler

Two mechanisms must be kept in sync:

- `connectionStatusSemaphore` (SemaphoreSlim(1,1)) serializes `OpenPort`/`ClosePort`/`RestartConnection`
- `restartPending` (int, Interlocked) coalesces rapid successive `PropertyChanged`-triggered restarts into one background task

`restartPending` exists because `NetworkSettings.CopyFrom()` sets three properties synchronously, firing three `PropertyChanged` events in quick succession. Without the flag, three `RestartConnection()` calls would be queued; the second/third would dispose `TCPIPBase` while the first's `ConnectLoopAsync` was still using it. The flag is set before the task starts and cleared after `RestartConnection()` completes — not before — so changes arriving mid-restart are also coalesced.

## Settings-Driven Reconnection

`CommunicationHandler` subscribes to:
- `networkSettings.PropertyChanged` → triggers `RestartConnection` on any IP/port/mode change
- `hsmsParameters.PropertyChanged` → only `T5` is applied live to `ConnectSeparationTimeout`; all other HSMS timers take effect on the next connect

## XML Library Loading

Equipment message definitions are loaded from XML files by `XMLParser` in `SecsGemBaseItems`. Results populate `LibraryManager`, which indexes stream/function message definitions. `CommunicationHandler` uses this index to validate and route incoming messages.
