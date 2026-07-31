namespace SecsGemScenarioEngine.Models;

public class ScenarioNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public NodeType Type { get; set; }
    public string? TransactionName { get; set; }
    public string? DisplayName { get; set; }
    public string? TransactionJson { get; set; }
    public bool UseReplyMessage { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}
