using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Serialization;
using SecsGemBaseItems.LibraryManager;
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
                    currentId = GetNextNodeId(scenario, currentId, isSuccess: true);
                    continue;
                }

                logger.LogInformation("Executing node {NodeId} ({NodeType}): {TransactionName}",
                    node.Id, node.Type, node.TransactionName ?? "N/A");

                var result = await ExecuteNodeAsync(node, token);

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

    private async Task<ScenarioExecutionResult> ExecuteNodeAsync(ScenarioNode node, CancellationToken token)
    {
        switch (node.Type)
        {
            case NodeType.Start:
            case NodeType.End:
                return new ScenarioExecutionResult { Success = true };

            case NodeType.SendAndWait:
                return await ExecuteSendAsync(node, token);

            case NodeType.Send:
                return await ExecuteSendAsync(node, token);

            case NodeType.Receive:
                return await ExecuteReceiveAsync(node, token);

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

    private async Task<ScenarioExecutionResult> ExecuteSendAsync(ScenarioNode node, CancellationToken token)
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

        if (!dataMessageHandler.CanSendMessage(transaction.PrimaryMessage))
        {
            return new ScenarioExecutionResult
            {
                Success = false,
                ErrorMessage = $"Cannot send '{node.TransactionName}': communication not ready"
            };
        }

        logger.LogInformation("Sending {TransactionName}...", node.TransactionName);
        var (error, reply) = await dataMessageHandler.SendDataMessage(transaction.PrimaryMessage, token);

        if (error == TransactionHandlerError.None || error == TransactionHandlerError.DoesNotRequireAReply)
        {
            if (reply != null)
                logger.LogInformation("Received reply for {TransactionName}", node.TransactionName);
            return new ScenarioExecutionResult { Success = true, CompletedSteps = 1 };
        }

        logger.LogError("Send {TransactionName} failed: {Error}", node.TransactionName, error);
        return new ScenarioExecutionResult
        {
            Success = false,
            ErrorMessage = $"Send '{node.TransactionName}' failed: {error}"
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

    private async Task<ScenarioExecutionResult> ExecuteReceiveAsync(ScenarioNode node, CancellationToken token)
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
        logger.LogInformation("Waiting to receive {MessageName}...", expectedMessage.Name);
        var received = await dataMessageHandler.WaitForReceivedMessage(
            msg => msg.Stream == expectedMessage.Stream && msg.Function == expectedMessage.Function,
            TimeSpan.FromSeconds(30),
            token);

        if (received == null)
        {
            return new ScenarioExecutionResult
            {
                Success = false,
                ErrorMessage = $"Timed out waiting to receive '{expectedMessage.Name}'"
            };
        }

        logger.LogInformation("Received matching {MessageName}", expectedMessage.Name);
        return new ScenarioExecutionResult { Success = true, CompletedSteps = 1 };
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
