# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Solution Overview

SecsGemBase is a C# (.NET 10) library suite implementing the **SECS/GEM** (SEMI Equipment Communications Standard / Generic Equipment Model) protocol for semiconductor manufacturing equipment. It is published as a set of NuGet packages to an internal Azure DevOps feed (`Products/SECS_GEM`). There is no runnable application — all projects are libraries.

## Build Commands

```bash
# Restore dependencies
dotnet restore SecsGemBase.sln

# Build (debug)
dotnet build SecsGemBase.sln

# Build (release / for packaging)
dotnet build SecsGemBase.sln --configuration Release
```

There are no test projects in this solution. CI runs on Azure Pipelines (`azure-pipelines.yml`) and triggers on `master` and `development` branches.

## Architecture

The solution is layered. Higher layers depend on lower ones; lower layers must not depend upward.

```
SecsGemMessageHandling   ← top-level orchestration: connects all pieces
        │
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
| `SecsGemBaseItems` | Data containers (`SecsGemMessage`, `SecsGemTransaction`, `SecsGemItem`), enums (`ControlState`, `MessageFormat`), `XMLParser` for loading equipment message libraries from XML, and `ISecsGemParameters` configuration. |
| `TCPIPBaseLibrary` | Raw TCP socket layer. `NetworkConnectionFactory` creates either an Active (equipment initiates) or Passive (host initiates) connection. Implements `INetworkConnection` with send/receive and connection lifecycle. |
| `SecsGemMessageHandling` | Assembles the full SECS/GEM communication stack. `CommunicationHandler` is the central class: it owns the network connection, drives the HSMS state machine, and dispatches parsed messages to callers via observables and events. |
| `SecsGemHelperClasses` | Stateless utilities shared across layers: message ID generation, SECS-II item builders, filtered/transient `IEventBus` implementations. |
| `Logging` | `ISecsGemLogger` abstraction plus `IMessageLogger` for structured message-level logging. `MessageStatus` / `MessageResult` define log outcomes. |

### XML Library Loading

Equipment capabilities are described in XML files (`.xml`) loaded by `XMLParser` in `SecsGemBaseItems`. The parsed result populates the `LibraryManager`, which indexes available stream/function message definitions. `CommunicationHandler` uses this index to validate and route incoming messages.

### Connection Modes (HSMS)

`NetworkConnectionFactory` accepts `INetworkSettings` which specifies Active vs. Passive mode and the host/port. Active mode = equipment connects to host; Passive mode = equipment listens and host connects.

### NuGet Packaging

Each project has a `.nuspec` file. Global package metadata (version `1.3.0`, symbol packages) is centralized in `Directory.Build.props`. Packages are pushed to the internal feed by the Azure Pipeline.

### Domain Vocabulary

The ReSharper settings (`SecsGemBase.sln.DotSettings`) register these abbreviations as single words for naming conventions: `HSMS`, `TCPIP`, `CEID`, `DVID`, `SV`, `EC`, `GEM`, `SECS`. Follow this casing in identifiers (e.g., `HSMSState`, not `HsmsState`).
