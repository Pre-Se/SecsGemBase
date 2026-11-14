using System.Diagnostics;
using SecsGemMessageHandling.Enums;
using SecsGemScenarioEngine.Models;
using TCPIPBaseLibrary.Interfaces;
using Xunit;
using static SecsGemScenarioEngine.Models.ScenarioNodeRunState;

namespace SecsGemScenarioEngine.Tests;

/// <summary>
/// The scenario node WaitForCommunication waits until data messages can be exchanged: HSMS Selected and
/// communications established (S1F13/S1F14) (GitLab secsgem/secsgembase#54).
/// </summary>
public class WaitForCommunicationTests
{
    private static readonly TimeSpan RunDeadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Succeeds_at_once_when_communication_is_established()
    {
        using var link = await Link.ConnectAsync(ConnectionMode.Passive);
        var wait = Scenario.Node(NodeType.WaitForCommunication, " 0 ");
        var progress = new ProgressLog();

        var result = await link.Simulator.Engine.ExecuteAsync(Scenario.Of(wait), deadline: TimeSpan.FromSeconds(5),
            progress: progress).WaitAsync(RunDeadline);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(progress.Reached(wait, Waiting));
    }

    [Fact]
    public async Task Waits_until_communication_is_established()
    {
        var port = Wait.GetFreePort();
        using var simulator = new Side(ConnectionMode.Passive, port);
        using var remote = new Side(ConnectionMode.Active, port);
        simulator.Communication.OpenPort();
        var wait = Scenario.Node(NodeType.WaitForCommunication, "20000");
        bool? readyWhenEstablished = null;
        var progress = new ProgressLog(report =>
        {
            if (report.NodeId == wait.Id && report.State == Succeeded) readyWhenEstablished = simulator.Ready;
        });

        var run = simulator.Engine.ExecuteAsync(Scenario.Of(wait), deadline: RunDeadline, progress: progress);
        await Wait.UntilAsync(() => progress.Reached(wait, Waiting), simulator.Describe);
        remote.Communication.OpenPort();
        var result = await run.WaitAsync(RunDeadline);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(readyWhenEstablished);
    }

    [Fact]
    public async Task Fails_when_the_timeout_expires()
    {
        using var simulator = new Side(ConnectionMode.Passive, Wait.GetFreePort());
        simulator.Communication.OpenPort();
        var wait = Scenario.Node(NodeType.WaitForCommunication, "300");
        var watch = Stopwatch.StartNew();

        var result = await simulator.Engine.ExecuteAsync(Scenario.Of(wait), deadline: RunDeadline)
            .WaitAsync(RunDeadline);

        Assert.False(result.Success);
        Assert.Equal(wait.Id, result.FailedNodeId);
        Assert.Equal("Communication not established within 300 ms", result.ErrorMessage);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(285), $"{watch.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task Zero_waits_until_the_run_deadline()
    {
        using var simulator = new Side(ConnectionMode.Passive, Wait.GetFreePort());
        simulator.Communication.OpenPort();
        var wait = Scenario.Node(NodeType.WaitForCommunication, "0");

        var result = await simulator.Engine.ExecuteAsync(Scenario.Of(wait), deadline: TimeSpan.FromSeconds(1))
            .WaitAsync(RunDeadline);

        Assert.False(result.Success);
        Assert.StartsWith("Scenario timed out", result.ErrorMessage);
    }

    /// <summary>
    /// A deselect makes HSMS NotSelected while communications stay established: data messages still cannot be
    /// exchanged, so the node keeps waiting.
    /// </summary>
    [Fact]
    public async Task Keeps_waiting_while_not_selected_although_communications_are_established()
    {
        using var link = await Link.ConnectAsync(ConnectionMode.Passive);
        var (simulator, remote) = (link.Simulator, link.Remote);
        await remote.Communication.SendStateMessage(remote.ControlMessages.CreateDeselectRequest());
        await Wait.UntilAsync(() => simulator.Control.HSMSStatus == HSMSStatus.NotSelected, link.Describe);
        Assert.True(simulator.Data.CommunicationsEstablished);
        var wait = Scenario.Node(NodeType.WaitForCommunication, "300");

        var result = await simulator.Engine.ExecuteAsync(Scenario.Of(wait), deadline: RunDeadline)
            .WaitAsync(RunDeadline);

        Assert.False(result.Success);
        Assert.Equal("Communication not established within 300 ms", result.ErrorMessage);
    }
}
