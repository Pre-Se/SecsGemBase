# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Solution Overview

SecsGemBase is a C# (.NET 10) library suite implementing the **SECS/GEM** (SEMI Equipment Communications Standard / Generic Equipment Model) protocol for semiconductor manufacturing equipment. It is published as a set of NuGet packages (AGPL-3.0 licensed). There is no runnable application — all projects are libraries.

## Build Commands

```bash
# Restore dependencies
dotnet restore SecsGemBase.sln

# Build (debug)
dotnet build SecsGemBase.sln

# Build (release / for packaging)
dotnet build SecsGemBase.sln --configuration Release

# Pack NuGet packages
dotnet pack SecsGemBase.sln -c Release
```

There are no test projects in this solution. A successful `dotnet build` is the only verification step.

## Shared Build Settings

`Directory.Build.props` centralizes all the `.csproj` files metadata:

- `TargetFramework` = `net10.0`
- `LangVersion` = `preview`
- `ImplicitUsings` = `enable`
- `Nullable` = `enable`
- Symbol packages (`IncludeSymbols`, `snupkg`), SourceLink, reproducible builds
- Versioning via **Nerdbank.GitVersioning**: base version in `version.json`, untagged builds get a prerelease suffix

Individual `.csproj` files only contain per-project `Title`, `Description`, and references. Do not duplicate shared settings in individual `.csproj` files.

## Architecture

The solution is layered. Higher layers depend on lower ones; lower layers must not depend upward.

```
SecsGemScenarioEngine        ← top: scenario execution, depends on SecsGemMessageHandling
  └── SecsGemMessageHandling ← full communication stack: depends on everything below
        ├── SecsGemBaseItems    ← core data structures, enums, XML parser
        ├── TCPIPBaseLibrary    ← TCP/IP client/server (Active/Passive modes)
        ├── SecsGemHelperClasses ← shared utilities: EventBus, ID generation
        └── Logging             ← logging interfaces + MessageStatus/Result enums
```

### Key Design Patterns

- **Result pattern**: `FluentResults.Result<T>` is used instead of exceptions for operation outcomes.
- **Reactive streams**: `System.Reactive` observables are used for incoming message streams.
- **MVVM / observable properties**: `CommunityToolkit.Mvvm` powers data-binding-ready state (e.g., `ConnectionState`, `ControlState`).
- **EventBus**: `SecsGemHelperClasses` provides transient and filtered event bus implementations used for decoupled event dispatch between layers.

### Project Responsibilities

| Project | Responsibility |
|---|---|
| `SecsGemBaseItems` | Data containers (`SecsGemDataMessage`, `SecsGemTransaction`, `SecsGemItem`), enums (`ControlState`, `SecsGemItemFormatType`), `XMLParser` for loading equipment message libraries from XML, and HSMS parameters configuration (`IHSMSParameters`). |
| `TCPIPBaseLibrary` | Raw TCP socket layer. `NetworkConnectionFactory` creates either an Active (equipment initiates) or Passive (host initiates) connection. Implements `ITCPIPBase` with send/receive and connection lifecycle. |
| `SecsGemMessageHandling` | Assembles the full SECS/GEM communication stack. `CommunicationHandler` is the central class: it owns the network connection, drives the HSMS state machine, and dispatches parsed messages to callers via observables and events. |
| `SecsGemHelperClasses` | Stateless utilities shared across layers: message ID generation, message parsing helpers, filtered/transient `IEventBus` implementations. |
| `Logging` | `ISecsMessageLogger` abstraction plus structured message-level logging. `MessageStatus` / `MessageResult` define log outcomes. |
| `SecsGemScenarioEngine` | Graph-based scenario execution engine for SECS/GEM message sequences (nodes, edges, execution service). |

### XML Library Loading

Equipment capabilities are described in XML files (`.xml`) loaded by `XMLParser` in `SecsGemBaseItems`. The parsed result populates the `LibraryManager`, which indexes available stream/function message definitions. `CommunicationHandler` uses this index to validate and route incoming messages.

### Connection Modes (HSMS)

`NetworkConnectionFactory` accepts `INetworkSettings` which specifies Active vs. Passive mode and the host/port. Active mode = equipment connects to host; Passive mode = equipment listens and host connects.

### NuGet Packaging

All package metadata is centralized in `Directory.Build.props` (license, source link, symbols). Versioning is automatic via **Nerdbank.GitVersioning** (see Shared Build Settings). Packages are created with `dotnet pack SecsGemBase.sln -c Release`.

### Domain Vocabulary

The ReSharper settings (`SecsGemBase.sln.DotSettings`) register these abbreviations as single words for naming conventions: `HSMS`, `TCPIP`, `CEID`, `DVID`, `SV`, `EC`, `GEM`, `SECS`. Follow this casing in identifiers (e.g., `HSMSState`, not `HsmsState`).
