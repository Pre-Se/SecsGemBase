using SecsGemScenarioEngine.Models;

namespace SecsGemScenarioEngine.Services;

public interface IScenarioExecutionService
{
    Task<ScenarioExecutionResult> ExecuteAsync(ScenarioGraph scenario, CancellationToken cancellation = default);
    void Cancel();
}

public class ScenarioExecutionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FailedNodeId { get; set; }
    public int CompletedSteps { get; set; }
}
