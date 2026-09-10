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
        HashSet<ILoggedDataMessage>? consumedAcrossRuns = null,
        TimeSpan? deadline = null)
    {
        using var deadlineCts = deadline is { } d ? new CancellationTokenSource(d) : null;
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadlineCts?.Token ?? CancellationToken.None);
        var token = cts.Token;

        try
        {
            var startNode = scenario.Nodes.FirstOrDefault(n => n.Type == NodeType.Start);
            if (startNode == null)
                return new ScenarioExecutionResult { Success = false, ErrorMessage = "No Start node found in scenario" };

            var nodeMap = scenario.Nodes.ToDictionary(n => n.Id);
            var runContext = new ScenarioRunContext(consumedAcrossRuns)
            {
                // No deadline => Receive nodes wait indefinitely (until the message arrives or the run
                // is cancelled). A deadline caps each wait and the whole run.
                ReceiveTimeout = deadline ?? Timeout.InfiniteTimeSpan
            };
            var completed = 0;

            // Tell the library auto-reply to stand down for every message type this scenario answers itself.
            using var replyClaim = replyGuard.BeginRun(CollectReceivedStreamFunctions(scenario).ToList());

            // Branch frontier: (source node id, target node id) edges still to traverse. A plain
            // linear scenario keeps exactly one entry at a time; a fork enqueues several; an And
            // node holds a branch back until every incoming edge has arrived.
            var frontier = new Queue<(string From, string To)>();
            var startEdges = scenario.Edges.Where(e => e.SourceNodeId == startNode.Id && !e.IsFailurePath).ToList();
            if (startEdges.Count == 0)
                return new ScenarioExecutionResult
                {
                    Success = false,
                    ErrorMessage = "Start node has no outgoing connection — wire it to the first step."
                };
            foreach (var edge in startEdges)
                frontier.Enqueue((startNode.Id, edge.TargetNodeId));

            var executed = new HashSet<string>();                       // non-And nodes run at most once per run
            var andArrivals = new Dictionary<string, HashSet<string>>(); // And node id -> source ids that reached it
            var firedAnds = new HashSet<string>();
            var safety = Math.Max(64, scenario.Nodes.Count * 8);

            logger.LogInformation("Starting scenario '{ScenarioName}'", scenario.Name);

            while (frontier.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (--safety < 0)
                    return new ScenarioExecutionResult
                    {
                        Success = false,
                        ErrorMessage = "Scenario did not settle (a loop without a Wait/Receive?)"
                    };

                var (from, id) = frontier.Dequeue();
                if (!nodeMap.TryGetValue(id, out var node))
                    continue;

                if (node.Type == NodeType.End)
                    continue; // this branch is done; keep draining the others

                if (node.Type == NodeType.And)
                {
                    if (firedAnds.Contains(id))
                        continue;

                    var required = scenario.Edges
                        .Where(e => e.TargetNodeId == id && !e.IsFailurePath)
                        .Select(e => e.SourceNodeId)
                        .ToHashSet();
                    if (!andArrivals.TryGetValue(id, out var arrived))
                        andArrivals[id] = arrived = [];
                    arrived.Add(from);

                    if (!required.IsSubsetOf(arrived))
                    {
                        logger.LogInformation("AND {NodeId}: {Have}/{Need} branches arrived", id, arrived.Count, required.Count);
                        continue; // hold this branch until the rest arrive
                    }

                    firedAnds.Add(id);
                    completed++;
                    logger.LogInformation("AND {NodeId}: all {Need} branches arrived — continuing", id, required.Count);
                    EnqueueSuccessors(scenario, frontier, id);
                    continue;
                }

                if (!executed.Add(id))
                    continue; // another branch already ran this node

                logger.LogInformation("Executing node {NodeId} ({NodeType}): {TransactionName}",
                    node.Id, node.Type, node.TransactionName ?? "N/A");

                var result = await ExecuteNodeAsync(node, runContext, token);

                if (!result.Success)
                {
                    var failureId = GetNextNodeId(scenario, id, isSuccess: false);
                    if (failureId != null)
                    {
                        logger.LogInformation("Node {NodeId} failed, following failure path", id);
                        frontier.Enqueue((id, failureId));
                        continue;
                    }

                    return new ScenarioExecutionResult
                    {
                        Success = false,
                        ErrorMessage = result.ErrorMessage,
                        FailedNodeId = node.Id,
                        CompletedSteps = completed
                    };
                }

                completed++;
                EnqueueSuccessors(scenario, frontier, id);
            }

            foreach (var (andId, arrived) in andArrivals)
            {
                if (!firedAnds.Contains(andId))
                    logger.LogWarning("AND {NodeId} never received all its branches ({Have} arrived) — scenario ended without passing it", andId, arrived.Count);
            }

            logger.LogInformation("Scenario '{ScenarioName}' completed ({Steps} steps)", scenario.Name, completed);
            return new ScenarioExecutionResult { Success = true, CompletedSteps = completed };
        }
        catch (OperationCanceledException)
        {
            if (deadlineCts?.IsCancellationRequested == true && !cancellation.IsCancellationRequested)
            {
                logger.LogWarning("Scenario '{ScenarioName}' timed out after {Seconds:0}s", scenario.Name, deadline!.Value.TotalSeconds);
                return new ScenarioExecutionResult { Success = false, ErrorMessage = $"Scenario timed out ({deadline.Value.TotalSeconds:0}s)" };
            }

            logger.LogInformation("Scenario '{ScenarioName}' was cancelled", scenario.Name);
            return new ScenarioExecutionResult { Success = false, ErrorMessage = "Cancelled" };
        }
    }

    private static string? GetNextNodeId(ScenarioGraph scenario, string nodeId, bool isSuccess)
    {
        return scenario.Edges
            .FirstOrDefault(e => e.SourceNodeId == nodeId && e.IsFailurePath == !isSuccess)
            ?.TargetNodeId;
    }

    private static void EnqueueSuccessors(ScenarioGraph scenario, Queue<(string From, string To)> frontier, string nodeId)
    {
        foreach (var edge in scenario.Edges.Where(e => e.SourceNodeId == nodeId && !e.IsFailurePath))
            frontier.Enqueue((nodeId, edge.TargetNodeId));
    }

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

        var received = await dataMessageHandler.WaitForReceivedMessage(
            msg => msg.Stream == expectedMessage.Stream
                   && msg.Function == expectedMessage.Function
                   && conditions.TrueForAll(c => c.Evaluate(msg)),
            runContext.ReceiveTimeout,
            token,
            // Don't let this Receive node re-consume a buffered message an earlier Receive already took —
            // each Receive in a chain must wait for its own inbound message.
            logged => !runContext.ConsumedMessages.Contains(logged));

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

        runContext.ConsumedMessages.Add(received);
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

    /// <summary>Per-run state threaded through node execution.</summary>
    private sealed class ScenarioRunContext
    {
        public ScenarioRunContext(HashSet<ILoggedDataMessage>? sharedConsumed)
        {
            ConsumedMessages = sharedConsumed ?? new HashSet<ILoggedDataMessage>(ReferenceEqualityComparer.Instance);
        }

        /// <summary>The message matched by the most recent Receive node (fallback source for bindings).</summary>
        public SecsGemDataMessage? LastReceived { get; set; }
        public uint? LastReceivedSystemBytes { get; set; }

        /// <summary>How long a Receive node waits: the run deadline, or infinite when none is set.</summary>
        public TimeSpan ReceiveTimeout { get; init; } = Timeout.InfiniteTimeSpan;

        /// <summary>Every message received so far this run, keyed by the Receive node's id.</summary>
        public Dictionary<string, SecsGemDataMessage> ReceivedByNode { get; } = [];

        /// <summary>Logged messages already consumed by a Receive node (reference identity). May be shared across loop iterations.</summary>
        public HashSet<ILoggedDataMessage> ConsumedMessages { get; }
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
