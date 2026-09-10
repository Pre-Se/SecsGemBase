using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Logging.Interfaces;
using Microsoft.Extensions.Logging;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Serialization;
using SecsGemBaseItems.LibraryManager;
using SecsGemBaseItems.Responders;
using SecsGemMessageHandling.Data_Handling;
using SecsGemMessageHandling.Enums;
using SecsGemScenarioEngine.Models;

namespace SecsGemScenarioEngine.Services;

public class ScenarioExecutionService : IScenarioExecutionService
{
    private readonly DataMessageHandler dataMessageHandler;
    private readonly ISecsGemLibraryManager libraryManager;
    private readonly ILogger<ScenarioExecutionService> logger;
    private readonly ScenarioReplyGuard replyGuard;
    private CancellationTokenSource? cts;

    public ScenarioExecutionService(
        DataMessageHandler dataMessageHandler,
        ISecsGemLibraryManager libraryManager,
        ILogger<ScenarioExecutionService> logger,
        ScenarioReplyGuard replyGuard)
    {
        this.dataMessageHandler = dataMessageHandler;
        this.libraryManager = libraryManager;
        this.logger = logger;
        this.replyGuard = replyGuard;
    }

    public void Cancel()
    {
        cts?.Cancel();
    }

    public async Task<ScenarioExecutionResult> ExecuteAsync(
        ScenarioGraph scenario,
        CancellationToken cancellation = default,
        ConcurrentDictionary<ILoggedDataMessage, byte>? consumedAcrossRuns = null,
        TimeSpan? deadline = null,
        IProgress<ScenarioNodeProgress>? progress = null)
    {
        using var deadlineCts = deadline is { } d ? new CancellationTokenSource(d) : null;
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadlineCts?.Token ?? CancellationToken.None);
        var token = cts.Token;
        var localCts = cts;

        var startNode = scenario.Nodes.FirstOrDefault(n => n.Type == NodeType.Start);
        if (startNode == null)
            return new ScenarioExecutionResult { Success = false, ErrorMessage = "No Start node found in scenario" };

        var startEdges = scenario.Edges.Where(e => e.SourceNodeId == startNode.Id && !e.IsFailurePath).ToList();
        if (startEdges.Count == 0)
            return new ScenarioExecutionResult
            {
                Success = false,
                ErrorMessage = "Start node has no outgoing connection — wire it to the first step."
            };

        var nodeMap = scenario.Nodes.ToDictionary(n => n.Id);
        var runContext = new ScenarioRunContext(consumedAcrossRuns)
        {
            // No deadline => Receive nodes wait indefinitely (until the message arrives or the run
            // is cancelled). A deadline caps each wait and the whole run.
            ReceiveTimeout = deadline ?? Timeout.InfiniteTimeSpan,
            Progress = progress
        };

        using var replyClaim = replyGuard.BeginRun(CollectReceivedStreamFunctions(scenario).ToList());

        // Concurrent branch execution: each outgoing path runs as its own async flow and awaits
        // independently, so two Receive nodes on a fork wait at the same time. An And node is a join
        // barrier — a branch registers its arrival and ends; the And spawns its successor once every
        // incoming branch has arrived.
        var executed = new ConcurrentDictionary<string, byte>();       // non-And nodes run at most once
        var andArrivals = new Dictionary<string, HashSet<string>>();   // And id -> source ids that arrived
        var firedAnds = new HashSet<string>();
        var andLock = new object();
        var branchLock = new object();
        var branches = new List<Task>();
        var completed = 0;
        ScenarioExecutionResult? hardFailure = null;

        logger.LogInformation("Starting scenario '{ScenarioName}'", scenario.Name);

        void FailHard(ScenarioExecutionResult failure)
        {
            lock (branchLock) hardFailure ??= failure;
            try { localCts.Cancel(); } catch (ObjectDisposedException) { }
        }

        void StartBranch(string fromId, string toId)
        {
            async Task Wrapped()
            {
                try { await RunBranchAsync(fromId, toId); }
                catch (OperationCanceledException) { /* run cancelled / timed out */ }
                catch (Exception ex) { FailHard(new ScenarioExecutionResult { Success = false, ErrorMessage = ex.Message }); }
            }

            var task = Wrapped();
            lock (branchLock) branches.Add(task);
        }

        async Task RunBranchAsync(string fromId, string currentId)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!nodeMap.TryGetValue(currentId, out var node))
                    return;

                if (node.Type == NodeType.End)
                    return; // this branch is done

                if (node.Type == NodeType.And)
                {
                    bool fire;
                    int have, need;
                    lock (andLock)
                    {
                        if (firedAnds.Contains(currentId)) return;
                        if (!andArrivals.TryGetValue(currentId, out var arrived))
                            andArrivals[currentId] = arrived = [];
                        arrived.Add(fromId);
                        var required = scenario.Edges
                            .Where(e => e.TargetNodeId == currentId && !e.IsFailurePath)
                            .Select(e => e.SourceNodeId)
                            .ToHashSet();
                        have = arrived.Count;
                        need = required.Count;
                        fire = required.IsSubsetOf(arrived);
                        if (fire) firedAnds.Add(currentId);
                    }

                    if (!fire)
                    {
                        logger.LogInformation("AND {NodeId}: {Have}/{Need} branches arrived", currentId, have, need);
                        progress?.Report(new ScenarioNodeProgress(currentId, ScenarioNodeRunState.Waiting, $"{have}/{need} branches"));
                        return; // hold this branch; another branch's arrival will fire the And
                    }

                    Interlocked.Increment(ref completed);
                    logger.LogInformation("AND {NodeId}: all {Need} branches arrived — continuing", currentId, need);
                    progress?.Report(new ScenarioNodeProgress(currentId, ScenarioNodeRunState.Succeeded));
                    foreach (var edge in Successors(scenario, currentId))
                        StartBranch(currentId, edge.TargetNodeId);
                    return;
                }

                if (!executed.TryAdd(currentId, 0))
                    return; // another branch already ran this node

                logger.LogInformation("Executing node {NodeId} ({NodeType}): {TransactionName}",
                    node.Id, node.Type, node.TransactionName ?? "N/A");
                progress?.Report(new ScenarioNodeProgress(currentId, ScenarioNodeRunState.Running));

                var result = await ExecuteNodeAsync(node, runContext, token);

                if (!result.Success)
                {
                    progress?.Report(new ScenarioNodeProgress(currentId, ScenarioNodeRunState.Failed, result.ErrorMessage));

                    var failureId = GetNextNodeId(scenario, currentId, isSuccess: false);
                    if (failureId != null)
                    {
                        logger.LogInformation("Node {NodeId} failed, following failure path", currentId);
                        fromId = currentId;
                        currentId = failureId;
                        continue;
                    }

                    FailHard(new ScenarioExecutionResult
                    {
                        Success = false,
                        ErrorMessage = result.ErrorMessage,
                        FailedNodeId = node.Id,
                        CompletedSteps = completed
                    });
                    return;
                }

                progress?.Report(new ScenarioNodeProgress(currentId, ScenarioNodeRunState.Succeeded));
                Interlocked.Increment(ref completed);

                var successors = Successors(scenario, currentId).ToList();
                if (successors.Count == 0)
                    return; // dead end

                for (var i = 1; i < successors.Count; i++)
                    StartBranch(currentId, successors[i].TargetNodeId); // fork
                fromId = currentId;
                currentId = successors[0].TargetNodeId;
            }
        }

        foreach (var edge in startEdges)
            StartBranch(startNode.Id, edge.TargetNodeId);

        // Wait for every branch, including ones forked mid-run or spawned by an And firing.
        while (true)
        {
            Task[] pending;
            lock (branchLock) pending = branches.Where(t => !t.IsCompleted).ToArray();
            if (pending.Length == 0) break;
            await Task.WhenAll(pending);
        }

        if (hardFailure != null)
            return hardFailure;

        if (token.IsCancellationRequested)
        {
            if (deadlineCts?.IsCancellationRequested == true && !cancellation.IsCancellationRequested)
            {
                logger.LogWarning("Scenario '{ScenarioName}' timed out after {Seconds:0}s", scenario.Name, deadline!.Value.TotalSeconds);
                return new ScenarioExecutionResult { Success = false, ErrorMessage = $"Scenario timed out ({deadline.Value.TotalSeconds:0}s)" };
            }

            logger.LogInformation("Scenario '{ScenarioName}' was cancelled", scenario.Name);
            return new ScenarioExecutionResult { Success = false, ErrorMessage = "Cancelled" };
        }

        lock (andLock)
        {
            foreach (var (andId, arrived) in andArrivals)
                if (!firedAnds.Contains(andId))
                    logger.LogWarning("AND {NodeId} never received all its branches ({Have} arrived) — scenario ended without passing it", andId, arrived.Count);
        }

        logger.LogInformation("Scenario '{ScenarioName}' completed ({Steps} steps)", scenario.Name, completed);
        return new ScenarioExecutionResult { Success = true, CompletedSteps = completed };
    }

    private static string? GetNextNodeId(ScenarioGraph scenario, string nodeId, bool isSuccess)
    {
        return scenario.Edges
            .FirstOrDefault(e => e.SourceNodeId == nodeId && e.IsFailurePath == !isSuccess)
            ?.TargetNodeId;
    }

    private static IEnumerable<ScenarioEdge> Successors(ScenarioGraph scenario, string nodeId) =>
        scenario.Edges.Where(e => e.SourceNodeId == nodeId && !e.IsFailurePath);

    /// <summary>The Stream/Function of every message a Receive node in this scenario is waiting for.</summary>
    private IEnumerable<(byte Stream, byte Function)> CollectReceivedStreamFunctions(ScenarioGraph scenario)
    {
        foreach (var node in scenario.Nodes.Where(n => n.Type == NodeType.Receive))
        {
            var transaction = FindTransaction(node);
            if (transaction == null) continue;
            var expected = node.UseReplyMessage ? transaction.ReplyMessage : transaction.PrimaryMessage;
            yield return (expected.Stream, expected.Function);
        }
    }

    private async Task<ScenarioExecutionResult> ExecuteNodeAsync(ScenarioNode node, ScenarioRunContext runContext, CancellationToken token)
    {
        switch (node.Type)
        {
            case NodeType.Start:
            case NodeType.End:
                return new ScenarioExecutionResult { Success = true };

            case NodeType.SendAndWait:
                return await ExecuteSendAsync(node, runContext, token);

            case NodeType.Send:
                return await ExecuteSendAsync(node, runContext, token);

            case NodeType.Receive:
                return await ExecuteReceiveAsync(node, runContext, token);

            case NodeType.Wait:
                return await ExecuteWaitAsync(node, token);

            case NodeType.And:
                return new ScenarioExecutionResult { Success = true }; // join logic handled in ExecuteAsync

            default:
                return new ScenarioExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"Unknown node type: {node.Type}"
                };
        }
    }

    private async Task<ScenarioExecutionResult> ExecuteSendAsync(ScenarioNode node, ScenarioRunContext runContext, CancellationToken token)
    {
        var transaction = FindTransaction(node);
        if (transaction == null)
        {
            return new ScenarioExecutionResult
            {
                Success = false,
                ErrorMessage = $"Transaction '{node.TransactionName}' not found in library"
            };
        }

        // A Send node with UseReplyMessage set is answering a message the sim received:
        // send the transaction's ReplyMessage (e.g. S6F12) rather than its primary, and reuse
        // the captured request's system bytes so it correlates as a real reply.
        var replyMode = node.UseReplyMessage;
        var message = replyMode ? transaction.ReplyMessage : transaction.PrimaryMessage;

        ApplyResponseBindings(node, message, runContext);

        if (!dataMessageHandler.CanSendMessage(message))
        {
            return new ScenarioExecutionResult
            {
                Success = false,
                ErrorMessage = $"Cannot send '{message.Name}': communication not ready"
            };
        }

        logger.LogInformation("Sending {MessageName}...", message.Name);

        var (error, reply) = replyMode && runContext.LastReceivedSystemBytes is { } systemBytes
            ? await dataMessageHandler.SendDataMessage(message, systemBytes, token)
            : await dataMessageHandler.SendDataMessage(message, token);

        if (error == TransactionHandlerError.None || error == TransactionHandlerError.DoesNotRequireAReply)
        {
            if (reply != null)
                logger.LogInformation("Received reply for {MessageName}", message.Name);

            // Capture what we sent so a later Send node can echo values back from it.
            runContext.ReceivedByNode[node.Id] = message;
            return new ScenarioExecutionResult { Success = true, CompletedSteps = 1 };
        }

        logger.LogError("Send {MessageName} failed: {Error}", message.Name, error);
        return new ScenarioExecutionResult
        {
            Success = false,
            ErrorMessage = $"Send '{message.Name}' failed: {error}"
        };
    }

    private async Task<ScenarioExecutionResult> ExecuteWaitAsync(ScenarioNode node, CancellationToken token)
    {
        // Wait node: TransactionName stores the delay in milliseconds
        if (int.TryParse(node.TransactionName, out var delayMs) && delayMs > 0)
        {
            logger.LogInformation("Waiting {DelayMs}ms...", delayMs);
            await Task.Delay(delayMs, token);
        }

        return new ScenarioExecutionResult { Success = true };
    }

    private async Task<ScenarioExecutionResult> ExecuteReceiveAsync(ScenarioNode node, ScenarioRunContext runContext, CancellationToken token)
    {
        var transaction = FindTransaction(node);
        if (transaction == null)
        {
            return new ScenarioExecutionResult
            {
                Success = false,
                ErrorMessage = $"Transaction '{node.TransactionName}' for Receive node not found"
            };
        }

        var expectedMessage = node.UseReplyMessage ? transaction.ReplyMessage : transaction.PrimaryMessage;
        var conditions = ResponderJson.DeserializeConditions(node.MatchConditionsJson);

        logger.LogInformation(
            "Waiting to receive {MessageName}{ConditionInfo}...",
            expectedMessage.Name,
            conditions.Count > 0 ? $" ({conditions.Count} condition(s))" : string.Empty);
        runContext.Progress?.Report(new ScenarioNodeProgress(node.Id, ScenarioNodeRunState.Waiting, $"waiting for {expectedMessage.Name}"));

        var received = await dataMessageHandler.WaitForReceivedMessage(
            msg => msg.Stream == expectedMessage.Stream
                   && msg.Function == expectedMessage.Function
                   && conditions.TrueForAll(c => c.Evaluate(msg)),
            runContext.ReceiveTimeout,
            token,
            // Atomically claim the message so exactly one Receive consumes it — whether that's an
            // earlier step in the chain or another branch waiting concurrently on the same S/F.
            logged => runContext.ConsumedMessages.TryAdd(logged, 0));

        if (received == null)
        {
            return new ScenarioExecutionResult
            {
                Success = false,
                ErrorMessage = conditions.Count > 0
                    ? $"Timed out waiting for '{expectedMessage.Name}' matching {conditions.Count} condition(s)"
                    : $"Timed out waiting to receive '{expectedMessage.Name}'"
            };
        }

        runContext.LastReceived = received.Data;
        runContext.LastReceivedSystemBytes = received.HeaderData.SystemBytes;
        runContext.ReceivedByNode[node.Id] = received.Data;
        logger.LogInformation("Received matching {MessageName}", expectedMessage.Name);
        return new ScenarioExecutionResult { Success = true, CompletedSteps = 1 };
    }

    /// <summary>
    /// Applies a Send node's <see cref="ScenarioNode.ResponseBindingsJson"/> to <paramref name="outgoing"/>.
    /// Each binding pulls from the message captured by its <see cref="ValueBinding.SourceNodeId"/> Receive node,
    /// or from the most recently received message when that id is unset/unknown.
    /// </summary>
    private void ApplyResponseBindings(ScenarioNode node, SecsGemDataMessage outgoing, ScenarioRunContext runContext)
    {
        if (string.IsNullOrWhiteSpace(node.ResponseBindingsJson))
            return;

        var bindings = ResponderJson.DeserializeBindings(node.ResponseBindingsJson);
        if (bindings.Count == 0)
            return;

        if (runContext.ReceivedByNode.Count == 0 && runContext.LastReceived is null)
        {
            logger.LogWarning(
                "Node {NodeId} has response bindings but no message has been received yet in this run", node.Id);
            return;
        }

        SecsGemDataMessage? Resolve(string? sourceNodeId) =>
            !string.IsNullOrEmpty(sourceNodeId) && runContext.ReceivedByNode.TryGetValue(sourceNodeId, out var m)
                ? m
                : runContext.LastReceived;

        foreach (var binding in bindings)
        {
            if (!binding.Apply(outgoing, Resolve))
                logger.LogWarning("Node {NodeId}: binding {Binding} could not be applied", node.Id, binding);
        }
    }

    /// <summary>Per-run state threaded through node execution. Touched by concurrent branch tasks — keep members thread-safe.</summary>
    private sealed class ScenarioRunContext
    {
        public ScenarioRunContext(ConcurrentDictionary<ILoggedDataMessage, byte>? sharedConsumed)
        {
            ConsumedMessages = sharedConsumed ?? new ConcurrentDictionary<ILoggedDataMessage, byte>(ReferenceEqualityComparer.Instance);
        }

        /// <summary>The message matched by the most recent Receive node (fallback source for bindings).</summary>
        public SecsGemDataMessage? LastReceived { get; set; }
        public uint? LastReceivedSystemBytes { get; set; }

        /// <summary>How long a Receive node waits: the run deadline, or infinite when none is set.</summary>
        public TimeSpan ReceiveTimeout { get; init; } = Timeout.InfiniteTimeSpan;

        /// <summary>Optional sink for live node-state updates (canvas feedback).</summary>
        public IProgress<ScenarioNodeProgress>? Progress { get; init; }

        /// <summary>Every message received so far this run, keyed by the Receive node's id.</summary>
        public ConcurrentDictionary<string, SecsGemDataMessage> ReceivedByNode { get; } = new();

        /// <summary>Inbound messages already claimed by a Receive node (reference identity). May be shared across loop iterations.</summary>
        public ConcurrentDictionary<ILoggedDataMessage, byte> ConsumedMessages { get; }
    }

    private SecsGemTransaction? FindTransaction(ScenarioNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.TransactionJson))
        {
            var tx = SecsGemTransactionJsonConverter.Deserialize(node.TransactionJson);
            if (tx != null) return tx;
        }

        if (string.IsNullOrWhiteSpace(node.TransactionName))
            return null;

        return libraryManager.Library.FirstOrDefault(t =>
            string.Equals(t.PrimaryMessage?.Name, node.TransactionName, StringComparison.OrdinalIgnoreCase));
    }
}
