namespace SecsGemScenarioEngine.Models;

public class ScenarioNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public NodeType Type { get; set; }
    public string? TransactionName { get; set; }
    public string? DisplayName { get; set; }
    public string? TransactionJson { get; set; }
    public bool UseReplyMessage { get; set; }

    /// <summary>
    /// Receive node only. Serialized <c>List&lt;MatchCondition&gt;</c> (see <c>SecsGemBaseItems.Responders</c>):
    /// the node only succeeds when the incoming message satisfies every condition.
    /// </summary>
    public string? MatchConditionsJson { get; set; }

    /// <summary>
    /// Send node only. Serialized <c>List&lt;ValueBinding&gt;</c>: values pulled from the message captured
    /// by the most recent upstream Receive node are written into this node's outgoing message before it is sent.
    /// </summary>
    public string? ResponseBindingsJson { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
}
