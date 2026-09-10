namespace SecsGemScenarioEngine.Models;

/// <summary>Live state of a scenario node during a run, for canvas feedback.</summary>
public enum ScenarioNodeRunState
{
    Idle,
    Running,
    Waiting,
    Succeeded,
    Failed
}

/// <summary>A single node-state change reported while a scenario runs.</summary>
/// <param name="NodeId">The node this applies to.</param>
/// <param name="State">Its new state.</param>
/// <param name="Detail">Optional text, e.g. "waiting for S6F11" or "1/2 branches".</param>
public sealed record ScenarioNodeProgress(string NodeId, ScenarioNodeRunState State, string? Detail = null);
