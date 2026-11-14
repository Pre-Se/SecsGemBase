# AGENTS.md

SecsGemBase is a C# library suite implementing the SECS/GEM protocol for semiconductor equipment. All projects are libraries — there is no runnable application.

## Build

```bash
dotnet restore SecsGemBase.sln
dotnet build SecsGemBase.sln
dotnet build SecsGemBase.sln --configuration Release
dotnet pack SecsGemBase.sln -c Release
dotnet test SecsGemBase.sln -c Release
```

xUnit test projects: `SecsGemBaseItems.Tests`, `TCPIPBaseLibrary.Tests`, `SecsGemMessageHandling.Tests` and `SecsGemScenarioEngine.Tests` (`IsPackable=false`: `dotnet pack` packs the whole solution). Many tests use real loopback sockets. There is no linter.

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

- `.github/workflows/ci.yml`: on push to `development` or `main`, on every pull request and by hand. Restore, Release build and `dotnet test` of the solution on Ubuntu and Windows (the TCP tests behave differently on Windows, e.g. resets and rebinding the passive port).
- `.github/workflows/publish.yml` ("Publish to NuGet"): on a `v*` tag or by hand ("Run workflow"). The job runs only for `development` or a `v*` tag; that guard only prevents accidental runs, because the `release` environment has no protection rules (deployment branches, reviewers) yet. Build, test, pack and push all six `SecsGemBase.*` packages to nuget.org with trusted publishing (`NuGet/login`, environment `release`). The version comes from `version.json` and the commit height (`1.0.0-build-<height>-g<commit>`), not from the tag. A published version cannot be deleted, only unlisted. The actions are pinned to commits because the job can obtain a NuGet API key. When `development` is retired (GitLab ticket 35), change the branch in the guard.
- Check workflow edits with `actionlint` before pushing: GitHub silently starts no job for an invalid file.
- `global.json` limits the SDK to 10.0 (the highest installed 10.0 feature band): the runners also have a newer SDK preinstalled, which would otherwise build with a different compiler (`LangVersion` is `preview`). The workflows install `10.0.x` (GitLab ticket 45).

## Domain Vocabulary (Naming)

The ReSharper settings register these as single-word abbreviations: `HSMS`, `TCPIP`, `CEID`, `DVID`, `SV`, `EC`, `GEM`, `SECS`. Always write `HSMSState`, `TCPIPClient`, etc. — never `HsmsState`.

## Thread Safety in CommunicationHandler

Two mechanisms must be kept in sync:

- `connectionStatusSemaphore` (SemaphoreSlim(1,1)) serializes `OpenPort`/`ClosePort`/`ClosePortIfCurrent`/`RestartConnection`/`TryDropConnection`/`ReopenPortAfterDrop` and the status update in `OnConnected`; `Dispose()` does not take it. The `…Locked` methods expect it to be held.
- `restartPending` (int, Interlocked) coalesces queued restarts (`PropertyChanged` of the network settings, `OnDisconnected`, failed sends) into one background task

`restartPending` exists because `NetworkSettings.CopyFrom()` sets three properties synchronously, firing three `PropertyChanged` events in quick succession, and because a lost connection often brings `OnDisconnected` and failed sends together. Without the flag, several `RestartConnection()` calls would be queued and each would replace the connection again. The flag is set before the task starts and cleared under the lock right after the old connection has been disposed, before the new one is built: requests up to that point are covered by this restart, later ones queue a new restart. That is right for `OnDisconnected` of the new connection and for settings changes (the new connection is built after the reset). A failed send that concerns the old connection but reaches the flag only after the reset restarts the new one as well, because restart requests are not yet bound to their connection (GitLab ticket 32). Clearing it only after `RestartConnection()` returned dropped an `OnDisconnected` of the new connection, and since `TCPIPClientBase` no longer reconnects by itself (GitLab ticket 5), nothing would reconnect.

## Aborting a Connection

`ITCPIPBase.Abort()` ends the connection with a TCP reset (RST) instead of the orderly close (FIN) of `Dispose()`, then disposes the object (GitLab ticket 53). `TCPIPClientBase` and `TCPIPServerBase` call `Socket.Close(0)` before the `TcpClient` is disposed: `TcpClient.Close()`/`Dispose()` always shuts the socket down first (via its `NetworkStream`), also with `LingerState` set. The default implementation is `Dispose()`, so a decorating `ITCPIPBase` must forward `Abort()` or it closes in order. `CommunicationHandler.ClosePort(bool abort)` and `RestartConnection(bool abort)` are overloads next to the parameterless methods (not optional parameters: method groups and builds against older packages).

## Scenario Nodes for Connection Loss

`NodeType.DropConnection` and `NodeType.WaitForCommunication` (GitLab ticket 54) take their parameter in ms from `TransactionName`, like the Wait node, but a value that is not a whole number ≥ 0 fails the node. They need the optional constructor parameters `CommunicationHandler` and `ControlMessageHandling` of `ScenarioExecutionService`.

- `DropConnection` calls the internal `CommunicationHandler.TryDropConnection` (visible to `SecsGemScenarioEngine` via `InternalsVisibleTo`: release both packages together). It checks for an established connection (Connected, and its loss not yet reported) and aborts it with a reset under the connection lock, either restarting it (downtime 0) or closing the port. After the downtime, or at once when the run is cancelled or times out, `ReopenPortAfterDrop` opens the port again, unless `ConnectionOn` was set in between (`portChanges` counts every set): then somebody opened or closed the port by hand, and a second `OpenPort` would start a second connection loop.
- Both calls go through `Task.Run`: `Dispose` of the old connection may wait up to 5 s for its loop, and EvenBetterFastSim starts runs on the UI thread, where the engine's continuations then run.
- `WaitForCommunication` waits for `HSMSStatus.Selected` and `DataMessageHandler.CommunicationsEstablished` (a deselect keeps the flag). `DropConnection` raises `OnDisconnect` before it returns, which clears the flag, so a following `WaitForCommunication` waits for the new connection. The flag is not bound to a connection, though: a handshake of the old connection that is still being handled can set it again (GitLab ticket 66).

## Send Failures

`SendAndLogMessage` and `SendStateMessage` report every write error as `MessageStatus.Failure` instead of throwing, and publish only written messages on `OnDataMessageOut`/`OnControlMessageOut` (GitLab ticket 14):

- `false` from `ITCPIPBase.SendData`, `SocketException`, `IOException` and `InvalidOperationException` (including `ObjectDisposedException`) queue a restart.
- `OperationCanceledException` means the connection object is being disposed, so a restart, `ClosePort` or `Dispose` is already replacing or closing it: no further restart. `TCPIPClientBase` and `TCPIPServerBase` throw it from `SendData` once their `Dispose()` has started, also for a write that it interrupts; a decorating `ITCPIPBase` may throw it for a replaced connection.
- Other exceptions still reach the caller. `TransactionHandler.SendTransaction` sends inside the `try` whose `finally` removes the entry in `ReplyExpectedMessages`; its catches cover only the wait for the reply.

`TransactionHandler.TryHandleDataMessageReply` completes a transaction with `TrySetResult` (GitLab ticket 12). It runs as a `Subscribe(Action)` handler on the event bus, where an exception ends the process. A second reply with the same system bytes, or a reply racing `RemoveAllTransactions` after a lost connection, is ignored: with a warning if the entry is still there, silently like any unmatched reply once `SendTransaction` or `RemoveAllTransactions` has removed it.

## Received Messages and Connections

`DataMessageHandler.WaitForReceivedMessage` (the scenario engine's Receive node) also looks 5 s back into a buffer, but only accepts messages received on the connection that is current when the wait starts, or a later one (GitLab ticket 55). `CommunicationHandler` numbers its connection objects in `BuildConnectionBase` and attaches the number to each received data message when it is parsed (`ConnectionNumberOf`, internal, a `ConditionalWeakTable`): the `OnDataReceived` bus of a replaced object may still deliver after the reconnect (ticket 7). A connection stops being current as soon as it raises `OnDisconnected` (bound per connection), not only when the queued restart has built the next one.

## Settings-Driven Reconnection

`CommunicationHandler` subscribes to:
- `networkSettings.PropertyChanged` → triggers `RestartConnection` on any IP/port/mode change
- `hsmsParameters.PropertyChanged` → only `T5` is applied live to `ConnectSeparationTimeout`; all other HSMS timers take effect on the next connect

## XML Library Loading

Equipment message definitions are loaded from XML files by `XMLParser` in `SecsGemBaseItems`. Results populate `LibraryManager`, which indexes stream/function message definitions. `CommunicationHandler` uses this index to validate and route incoming messages.
