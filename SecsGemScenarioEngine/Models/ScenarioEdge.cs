namespace SecsGemScenarioEngine.Models;

public class ScenarioEdge
{
    public string SourceNodeId { get; set; } = string.Empty;
    public string TargetNodeId { get; set; } = string.Empty;
    public bool IsFailurePath { get; set; }
}
