using Logging.Interfaces;
using SecsGemScenarioEngine.Models;

namespace SecsGemScenarioEngine.Services;

public interface IScenarioExecutionService
{
    /// <param name="consumedAcrossRuns">
    /// Optional shared set of already-consumed inbound messages. Pass the same instance to every
    /// iteration of a loop so a Receive node never re-consumes a message an earlier iteration already took.
    /// </param>
    /// <param name="deadline">Optional overall run deadline; the scenario fails if it doesn't finish in time.</param>
    Task<ScenarioExecutionResult> ExecuteAsync(
        ScenarioGraph scenario,
        CancellationToken cancellation = default,
        HashSet<ILoggedDataMessage>? consumedAcrossRuns = null,
        TimeSpan? deadline = null);
    void Cancel();
}

public class ScenarioExecutionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FailedNodeId { get; set; }
    public int CompletedSteps { get; set; }
}
