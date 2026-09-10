namespace SecsGemScenarioEngine.Models;

public enum NodeType
{
    Start,
    SendAndWait,
    Send,
    End,
    Condition,
    Wait,
    Receive,

    /// <summary>Join node: only continues once every incoming connection has been reached.</summary>
    And
}
