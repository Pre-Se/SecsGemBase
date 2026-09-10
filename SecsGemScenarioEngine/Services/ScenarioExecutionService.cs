using System.Collections.ObjectModel;
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
    private CancellationTokenSource? cts;

    public ScenarioExecutionService(
        DataMessageHandler dataMessageHandler,
        ISecsGemLibraryManager libraryManager,
        ILogger<ScenarioExecutionService> logger)
    {
        this.dataMessageHandler = dataMessageHandler;
        this.libraryManager = libraryManager;
        this.logger = logger;
    }

    public void Cancel()
    {
        cts?.Cancel();
    }

    public async Task<ScenarioExecutionResult> ExecuteAsync(ScenarioGraph scenario, CancellationToken cancellation = default)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var token = cts.Token;

        try
        {
            var startNode = scenario.Nodes.FirstOrDefault(n => n.Type == NodeType.Start);
            if (startNode == null)
                return new ScenarioExecutionResult { Success = false, ErrorMessage = "No Start node found in scenario" };

            var nodeMap = scenario.Nodes.ToDictionary(n => n.Id);
            var visited = new HashSet<string>();
            var runContext = new ScenarioRunContext();
            string? currentId = startNode.Id;
            int completed = 0;

            logger.LogInformation("Starting scenario '{ScenarioName}'", scenario.Name);

            while (currentId != null)
            {
                token.ThrowIfCancellationRequested();

                if (!visited.Add(currentId))
                    return new ScenarioExecutionResult { Success = false, ErrorMessage = "Cycle detected in scenario" };

                if (!nodeMap.TryGetValue(currentId, out var node)) break;

                if (node.Type == NodeType.End)
                {
                    logger.LogInformation("Scenario '{ScenarioName}' completed ({Steps} steps)", scenario.Name, completed);
                    return new ScenarioExecutionResult { Success = true, CompletedSteps = completed };
                }

                if (node.Type == NodeType.Start)
                {
                    var afterStart = GetNextNodeId(scenario, currentId, isSuccess: true);
                    if (afterStart == null)
                        return new ScenarioExecutionResult
                        {
                            Success = false,
                            ErrorMessage = "Start node has no outgoing connection — wire it to the first step."
                        };
                    currentId = afterStart;
                    continue;
                }

                logger.LogInformation("Executing node {NodeId} ({NodeType}): {TransactionName}",
                    node.Id, node.Type, node.TransactionName ?? "N/A");

                var result = await ExecuteNodeAsync(node, runContext, token);

                if (!result.Success)
                {
                    var failureId = GetNextNodeId(scenario, currentId, isSuccess: false);
                    if (failureId != null)
                    {
                        logger.LogInformation("Node {NodeId} failed, following failure path", currentId);
                        currentId = failureId;
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
                currentId = GetNextNodeId(scenario, currentId, isSuccess: true);
            }

            logger.LogInformation("Scenario '{ScenarioName}' completed ({Steps} steps)", scenario.Name, completed);
            return new ScenarioExecutionResult { Success = true, CompletedSteps = completed };
        }
        catch (OperationCanceledException)
        {
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
            TimeSpan.FromSeconds(30),
            token);

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
        logger.LogInformation("Received matching {MessageName}", expectedMessage.Name);
        return new ScenarioExecutionResult { Success = true, CompletedSteps = 1 };
    }

    /// <summary>
    /// Applies a Send node's <see cref="ScenarioNode.ResponseBindingsJson"/> to <paramref name="outgoing"/>,
    /// pulling values from the message captured by the most recent upstream Receive node.
    /// </summary>
    private void ApplyResponseBindings(ScenarioNode node, SecsGemDataMessage outgoing, ScenarioRunContext runContext)
    {
        if (string.IsNullOrWhiteSpace(node.ResponseBindingsJson))
            return;

        var bindings = ResponderJson.DeserializeBindings(node.ResponseBindingsJson);
        if (bindings.Count == 0)
            return;

        if (runContext.LastReceived is null)
        {
            logger.LogWarning(
                "Node {NodeId} has response bindings but no message has been received yet in this run", node.Id);
            return;
        }

        foreach (var binding in bindings)
        {
            if (!binding.Apply(outgoing, runContext.LastReceived))
                logger.LogWarning("Node {NodeId}: binding {Binding} could not be applied", node.Id, binding);
        }
    }

    /// <summary>Per-run state threaded through node execution: the message the most recent Receive node matched.</summary>
    private sealed class ScenarioRunContext
    {
        public SecsGemDataMessage? LastReceived { get; set; }
        public uint? LastReceivedSystemBytes { get; set; }
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
