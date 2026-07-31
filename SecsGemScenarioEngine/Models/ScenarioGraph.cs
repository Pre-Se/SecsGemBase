namespace SecsGemScenarioEngine.Models;

public class ScenarioGraph
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<ScenarioNode> Nodes { get; set; } = [];
    public List<ScenarioEdge> Edges { get; set; } = [];
}
