namespace SecsGemScenarioEngine.Models;

/// <remarks>Applications store the type as a number (EvenBetterFastSim does): only append, never reorder.</remarks>
public enum NodeType
{
    Start,
    SendAndWait,
    Send,
    End,
    Condition,

    /// <summary>
    /// Waits the time in <see cref="ScenarioNode.TransactionName"/>, in ms, then succeeds. The time is read with
    /// <c>int.TryParse</c> (<c>NumberStyles.Integer</c>, invariant culture): a whole number from 0 to 2147483647 in
    /// the digits 0-9; white space around it and a leading sign are accepted. 0 does not wait. A text that cannot be
    /// read this way and a negative number do not wait either; the node still succeeds and logs a warning.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="DropConnection"/> and <see cref="WaitForCommunication"/>, which take no sign and fail on a
    /// parameter they cannot read: scenarios made before the warning existed keep running as they did.
    /// </remarks>
    Wait,
    Receive,

    /// <summary>Join node: only continues once every incoming connection has been reached.</summary>
    And,

    /// <summary>
    /// Simulates a connection loss: aborts the established connection with a TCP reset (RST).
    /// <see cref="ScenarioNode.TransactionName"/> is the downtime in ms, a whole number ≥ 0. With 0 a new connection
    /// is set up right away; with more the port stays closed that long and is then opened again. If the run is
    /// cancelled or times out during the downtime, the port is opened again at once; if somebody opened or closed the
    /// port in the meantime, the node leaves it as it is. Fails if no connection is established (also when the
    /// connection has reported its end and its restart has not run yet) or the downtime is not such a number.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The other side sees at least the downtime. The connection comes back with the next attempt of the active
    /// side: after a refused attempt that is T5 later (HSMS parameter, 10 s by default).</item>
    /// <item>Replies the stack sends by itself (S1F14, S1F4, S2F34/36/38 and others) are not held back. Whether one
    /// that is due right before the drop still goes out is not guaranteed.</item>
    /// <item>Library auto-replies are held back only for the Stream/Function of the run's Receive nodes, and then for
    /// the whole run.</item>
    /// <item>Follow it with <see cref="WaitForCommunication"/> before sending: until communication is established
    /// again, Send nodes fail (unless the HSMS parameter IgnoreState is set).</item>
    /// </list>
    /// </remarks>
    DropConnection = 8,

    /// <summary>
    /// Waits until data messages can be exchanged: HSMS Selected and communications established (S1F13/S1F14).
    /// Succeeds right away if they already are. <see cref="ScenarioNode.TransactionName"/> is the timeout in ms, a
    /// whole number ≥ 0; 0 waits as long as the run may take. Fails when the timeout expires or the parameter is not
    /// such a number.
    /// </summary>
    /// <remarks>
    /// After a <see cref="DropConnection"/> it waits for the new connection: the drop ends the established
    /// communication before it returns. Exception: an S1F13/S1F14 handshake of the old connection that is still being
    /// handled at the drop can mark communication as established again (GitLab secsgem/secsgembase#66). When the other
    /// side drops the connection, the old state counts until the restart after the loss has run: a node that starts
    /// before then succeeds on it.
    /// </remarks>
    WaitForCommunication = 9
}
