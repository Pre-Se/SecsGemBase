# AGENTS.md

SecsGemBase is a C# library suite implementing the SECS/GEM protocol for semiconductor equipment. All projects are libraries — there is no runnable application.

## Build

```bash
dotnet restore SecsGemBase.sln
dotnet build SecsGemBase.sln
dotnet build SecsGemBase.sln --configuration Release
dotnet pack SecsGemBase.sln -c Release
```

There are no tests, no linter, and no typecheck in this repo. A successful `dotnet build` is the only verification step.

## Shared Build Settings (Directory.Build.props)

`Directory.Build.props` centralizes all the `.csproj` files metadata:

- `TargetFramework` = `net10.0`
- `LangVersion` = `preview`
- `ImplicitUsings` = `enable`
- `Nullable` = `enable`
- Symbol packages (`IncludeSymbols`, `SymbolPackageFormat=snupkg`), SourceLink (`EmbedUntrackedSources`), reproducible builds
- Versioning via **Nerdbank.GitVersioning**: base version in `version.json`; untagged builds get a prerelease suffix with the git commit hash.

Individual `.csproj` files contain only per-project `Title`, `Description`, and references.

## Project Dependencies (layered — lower layers must not depend upward)

```
SecsGemScenarioEngine        ← top: depends on SecsGemMessageHandling
  └── SecsGemMessageHandling ← depends on SecsGemBaseItems, TCPIPBaseLibrary, SecsGemHelperClasses, Logging
        ├── SecsGemBaseItems    ← depends on SecsGemHelperClasses
        ├── TCPIPBaseLibrary    ← depends on Logging, SecsGemHelperClasses
        ├── Logging             ← depends on SecsGemBaseItems
        └── SecsGemHelperClasses ← no project deps (leaf)
```

## Key NuGet Dependencies

- `FluentResults` (Result pattern — used instead of throwing exceptions)
- `System.Reactive` (observables for incoming message streams)
- `CommunityToolkit.Mvvm` (observable properties for connection state)
- `Microsoft.Extensions.Logging.Abstractions`

Build-time only (never appear in the produced packages): `Nerdbank.GitVersioning`, `DotNet.ReproducibleBuilds`, `Microsoft.SourceLink.GitHub`.

## CI

No CI pipeline is configured in this repo. Packages are published manually: `dotnet pack SecsGemBase.sln -c Release` then push the `.nupkg`/`.snupkg` files to nuget.org.

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
