using SecsGemMessageHandling.Enums;
using SecsGemScenarioEngine.Models;
using TCPIPBaseLibrary.Interfaces;
using Xunit;
using static SecsGemScenarioEngine.Models.ScenarioNodeRunState;

namespace SecsGemScenarioEngine.Tests;

/// <summary>
/// The scenario node DropConnection aborts the connection with a TCP reset and, with a downtime, keeps the port closed
/// for that long (GitLab secsgem/secsgembase#54, customer request in secsgem/evencoolerfastsim#25).
/// </summary>
public class DropConnectionTests
{
    private static readonly TimeSpan RunDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A Receive node reports Waiting before it subscribes, and a message published in between can miss both the
    /// subscription and the 5-s buffer (GitLab secsgem/secsgembase#52). The tests send only this long after Waiting.
    /// </summary>
    private static readonly TimeSpan UntilListening = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The customer's case: an event report triggers the drop, and once communication is back the other side has to
    /// send event Y again. The other side sees a reset; WaitForCommunication succeeds only on the new connection, ready
    /// for data messages; the Receive for Y does not take the Y that the old connection delivered and nobody consumed,
    /// although it is still in the 5-s buffer (GitLab secsgem/secsgembase#55).
    /// </summary>
    [Theory]
    [InlineData(ConnectionMode.Passive, 0)]
    [InlineData(ConnectionMode.Passive, 1500)]
    [InlineData(ConnectionMode.Active, 0)]
    [InlineData(ConnectionMode.Active, 1500)]
    public async Task Drop_then_wait_for_communication_then_receive_the_event_sent_again(
        ConnectionMode simulatorMode, int downtimeMs)
    {
        using var link = await Link.ConnectAsync(simulatorMode);
        var (simulator, remote) = (link.Simulator, link.Remote);
        var trigger = Scenario.Receive(41);
        var drop = Scenario.Node(NodeType.DropConnection, downtimeMs.ToString());
        var wait = Scenario.Node(NodeType.WaitForCommunication, "20000");
        var again = Scenario.Receive(42);
        int? connectsWhenEstablished = null;
        bool? readyWhenEstablished = null;
        var progress = new ProgressLog(report =>
        {
            if (report.NodeId != wait.Id || report.State != Succeeded) return;
            connectsWhenEstablished = simulator.Connects;
            readyWhenEstablished = simulator.Ready;
        });

        // Y on the old connection, before the run: nothing consumes it, and it is still buffered when `again` starts.
        await remote.SendEventAsync(42);
        var run = simulator.Engine.ExecuteAsync(Scenario.Of(trigger, drop, wait, again), deadline: RunDeadline,
            progress: progress);
        await Wait.UntilAsync(() => progress.Reached(trigger, Waiting), link.Describe);
        await Task.Delay(UntilListening);
        await remote.SendEventAsync(41);
        await link.UntilReadyAsync(connects: 2);
        await Wait.UntilAsync(() => progress.Reached(again, Waiting), link.Describe);
        // Also the time a Receive that wrongly took the old Y from the buffer would need to succeed.
        await Task.Delay(UntilListening);
        Assert.False(progress.Reached(again, Succeeded), "the Receive after the drop took the event of the old connection");
        await remote.SendEventAsync(42);
        var result = await run.WaitAsync(RunDeadline);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, connectsWhenEstablished);
        Assert.True(readyWhenEstablished);
        // ReceiveDataLoopAsync logs a reset with the socket error, a close (0 bytes) without.
        var ends = remote.TcpLog.Messages.Where(m => m.StartsWith("Connection was reset by remote machine")).ToList();
        Assert.Single(ends);
        Assert.StartsWith("Connection was reset by remote machine: ", ends[0]);
        if (downtimeMs > 0)
        {
            // From the drop (OnDisconnect) to the next connect. 100 ms for the timer resolution (15.6 ms on Windows):
            // without the downtime the outage is far below 200 ms.
            Assert.True(simulator.LastOutage >= TimeSpan.FromMilliseconds(downtimeMs - 100),
                $"outage {simulator.LastOutage.TotalMilliseconds:0} ms");
        }

        await link.UntilReadyAsync(connects: 2);
    }

    /// <summary>A Receive waiting in another branch survives the drop and gets an event of the new connection.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1500)]
    public async Task Receive_in_a_parallel_branch_gets_the_event_sent_after_the_drop(int downtimeMs)
    {
        using var link = await Link.ConnectAsync(ConnectionMode.Passive);
        var (simulator, remote) = (link.Simulator, link.Remote);
        var parallel = Scenario.Receive(7);
        var trigger = Scenario.Receive(42);
        var drop = Scenario.Node(NodeType.DropConnection, downtimeMs.ToString());
        var wait = Scenario.Node(NodeType.WaitForCommunication, "20000");
        var progress = new ProgressLog();

        var run = simulator.Engine.ExecuteAsync(Scenario.Of([parallel], [trigger, drop, wait]), deadline: RunDeadline,
            progress: progress);
        await Wait.UntilAsync(() => progress.Reached(parallel, Waiting) && progress.Reached(trigger, Waiting),
            link.Describe);
        await Task.Delay(UntilListening);
        await remote.SendEventAsync(42);
        await Wait.UntilAsync(() => progress.Reached(wait, Succeeded), link.Describe);
        await link.UntilReadyAsync(connects: 2);
        Assert.False(progress.Reached(parallel, Succeeded));
        await remote.SendEventAsync(7);
        var result = await run.WaitAsync(RunDeadline);

        Assert.True(result.Success, result.ErrorMessage);
    }

    /// <summary>When the run is cancelled or times out during the downtime, the node opens the port again.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Port_opens_again_when_the_run_ends_during_the_downtime(bool byDeadline)
    {
        using var link = await Link.ConnectAsync(ConnectionMode.Passive);
        var simulator = link.Simulator;
        var drop = Scenario.Node(NodeType.DropConnection, "60000");
        var progress = new ProgressLog();
        using var cancellation = new CancellationTokenSource();

        var run = simulator.Engine.ExecuteAsync(Scenario.Of(drop), cancellation.Token,
            deadline: byDeadline ? TimeSpan.FromSeconds(2) : null, progress: progress);
        await Wait.UntilAsync(() => progress.Reached(drop, Waiting), link.Describe);
        Assert.False(simulator.Communication.ConnectionOn);
        if (!byDeadline) cancellation.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Success);
        Assert.StartsWith(byDeadline ? "Scenario timed out" : "Cancelled", result.ErrorMessage);
        Assert.True(simulator.Communication.ConnectionOn);
        await link.UntilReadyAsync(connects: 2);
    }

    /// <summary>
    /// Somebody opens the port during the downtime (Connect in EvenBetterFastSim): the node leaves it alone. Opening
    /// it a second time would start a second connection loop on the same TCP object.
    /// </summary>
    [Fact]
    public async Task Port_opened_by_hand_during_the_downtime_is_not_opened_again()
    {
        using var link = await Link.ConnectAsync(ConnectionMode.Passive);
        var simulator = link.Simulator;
        var drop = Scenario.Node(NodeType.DropConnection, "1500");
        var progress = new ProgressLog();

        var run = simulator.Engine.ExecuteAsync(Scenario.Of(drop), deadline: RunDeadline, progress: progress);
        await Wait.UntilAsync(() => progress.Reached(drop, Waiting), link.Describe);
        await Task.Run(simulator.Communication.OpenPort);
        await link.UntilReadyAsync(connects: 2);
        var result = await run.WaitAsync(RunDeadline);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("Port left as it is: it was opened or closed during the downtime", simulator.EngineLog.Messages);
        // Opening it again would have set PortOpen before the run returned.
        Assert.Equal(ConnectionStatus.Connected, simulator.Communication.ConnectionStatus);
        await Task.Delay(500);
        await link.UntilReadyAsync(connects: 2);
        // A second loop on the same TCP object would have logged this by now.
        Assert.DoesNotContain("Connection loop is already running.", simulator.TcpLog.Messages);
    }

    /// <summary>Somebody closes the port during the downtime (Disconnect): it stays closed afterwards.</summary>
    [Fact]
    public async Task Port_closed_by_hand_during_the_downtime_stays_closed()
    {
        using var link = await Link.ConnectAsync(ConnectionMode.Passive);
        var (simulator, remote) = (link.Simulator, link.Remote);
        var drop = Scenario.Node(NodeType.DropConnection, "1000");
        var progress = new ProgressLog();

        var run = simulator.Engine.ExecuteAsync(Scenario.Of(drop), deadline: RunDeadline, progress: progress);
        await Wait.UntilAsync(() => progress.Reached(drop, Waiting), link.Describe);
        await Task.Run(() => simulator.Communication.ClosePort());
        var result = await run.WaitAsync(RunDeadline);
        // The remote side tries again every T5 (200 ms) and would be connected by now if the port were open.
        await Task.Delay(1000);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("Port left as it is: it was opened or closed during the downtime", simulator.EngineLog.Messages);
        Assert.False(simulator.Communication.ConnectionOn);
        Assert.Equal(1, simulator.Connects);
        Assert.NotEqual(ConnectionStatus.Connected, remote.Communication.ConnectionStatus);
    }

    /// <summary>Without an established connection the node fails and changes nothing: the port stays open.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1000")]
    public async Task Fails_without_an_established_connection(string downtime)
    {
        using var simulator = new Side(ConnectionMode.Passive, Wait.GetFreePort());
        simulator.Communication.OpenPort();
        var drop = Scenario.Node(NodeType.DropConnection, downtime);

        var result = await simulator.Engine.ExecuteAsync(Scenario.Of(drop), deadline: RunDeadline)
            .WaitAsync(RunDeadline);

        Assert.False(result.Success);
        Assert.Equal(drop.Id, result.FailedNodeId);
        Assert.Equal("DropConnection: no connection established", result.ErrorMessage);
        Assert.True(simulator.Communication.ConnectionOn);
        Assert.Equal(ConnectionStatus.PortOpen, simulator.Communication.ConnectionStatus);
    }

    /// <summary>
    /// A parameter that is not a whole number ≥ 0 fails the node instead of being read as 0 (unlike the Wait node, see
    /// secsgem/evencoolerfastsim#18). The connection is established, so only the parameter can make it fail.
    /// </summary>
    [Theory]
    [InlineData(NodeType.DropConnection, "5 s")]
    [InlineData(NodeType.DropConnection, "-1")]
    [InlineData(NodeType.DropConnection, "1.5")]
    [InlineData(NodeType.DropConnection, "")]
    [InlineData(NodeType.DropConnection, null)]
    [InlineData(NodeType.WaitForCommunication, "abc")]
    [InlineData(NodeType.WaitForCommunication, "-1")]
    [InlineData(NodeType.WaitForCommunication, "99999999999")]
    public async Task Invalid_parameter_fails_the_node(NodeType type, string? parameter)
    {
        using var link = await Link.ConnectAsync(ConnectionMode.Passive);
        var node = Scenario.Node(type, parameter);

        var result = await link.Simulator.Engine.ExecuteAsync(Scenario.Of(node), deadline: RunDeadline)
            .WaitAsync(RunDeadline);

        Assert.False(result.Success);
        Assert.Equal(node.Id, result.FailedNodeId);
        Assert.Equal($"{type}: '{parameter}' is not a {(type == NodeType.DropConnection ? "downtime" : "timeout")} " +
                     "in ms (a whole number ≥ 0)", result.ErrorMessage);
        await link.UntilReadyAsync(connects: 1);
    }

    /// <summary>A ScenarioExecutionService without CommunicationHandler or ControlMessageHandling fails these nodes.</summary>
    [Theory]
    [InlineData(NodeType.DropConnection, "DropConnection needs the CommunicationHandler")]
    [InlineData(NodeType.WaitForCommunication, "WaitForCommunication needs the ControlMessageHandling")]
    public async Task Fails_without_the_handlers(NodeType type, string error)
    {
        using var simulator = new Side(ConnectionMode.Passive, Wait.GetFreePort(), withHandlersForEngine: false);
        var node = Scenario.Node(type, "0");

        var result = await simulator.Engine.ExecuteAsync(Scenario.Of(node), deadline: RunDeadline)
            .WaitAsync(RunDeadline);

        Assert.False(result.Success);
        Assert.Equal(node.Id, result.FailedNodeId);
        Assert.StartsWith(error, result.ErrorMessage);
    }
}
