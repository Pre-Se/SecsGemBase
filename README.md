# SecsGemBase

A C# (.NET 10) library suite implementing the **SECS/GEM** (SEMI Equipment
Communications Standard / Generic Equipment Model) protocol for semiconductor
manufacturing equipment.

The suite is layered and published as a set of NuGet packages:

| Package | Responsibility |
|---|---|
| `SecsGemBase.BaseItems` | Core data structures, enums, and the XML equipment-library parser. |
| `SecsGemBase.TCPIPBaseLibrary` | Raw TCP/IP client/server layer (HSMS Active / Passive modes). |
| `SecsGemBase.HelperClasses` | Shared utilities: message ID generation, event bus. |
| `SecsGemBase.Logging` | Structured message-level logging abstractions. |
| `SecsGemBase.MessageHandling` | Full SECS/GEM communication stack and HSMS state machine. |
| `SecsGemBase.ScenarioEngine` | Graph-based scenario execution for SECS/GEM message sequences. |

## Install

```bash
dotnet add package SecsGemBase.MessageHandling --prerelease
```

Lower-layer packages come in transitively. Reference `SecsGemBase.ScenarioEngine`
instead if you need the scenario engine.

## Build from source

```bash
dotnet build SecsGemBase.sln -c Release
```

Requires the .NET 10 SDK. There are no test projects; a successful build is the
verification step.

## License

AGPL-3.0-only. See [LICENSE](LICENSE). Consuming these packages places the
consuming work under the AGPL's terms.
