using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using SecsGemScenarioEngine.Models;
using TCPIPBaseLibrary.Interfaces;
using Xunit;

namespace SecsGemScenarioEngine.Tests;

/// <summary>
/// The Wait node waits the time in TransactionName (ms). A value it cannot read never made it fail, and that stays;
/// but the run now says so instead of looking like a wait that took no time (GitLab secsgem/evencoolerfastsim#18).
/// </summary>
public class WaitNodeTests
{
    private static readonly TimeSpan RunDeadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Waits_the_time_in_milliseconds()
    {
        using var simulator = new Side(ConnectionMode.Passive, Wait.GetFreePort());
        var wait = Scenario.Node(NodeType.Wait, "300");
        var watch = Stopwatch.StartNew();

        var result = await simulator.Engine.ExecuteAsync(Scenario.Of(wait), deadline: RunDeadline).WaitAsync(RunDeadline);

        Assert.True(result.Success, result.ErrorMessage);
        // 285 ms: the timer may fire a little early (15.6 ms resolution on Windows).
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(285), $"{watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.Contains((LogLevel.Information, "Waiting 300ms..."), simulator.EngineLog.Entries);
        Assert.DoesNotContain(simulator.EngineLog.Entries, entry => entry.Level == LogLevel.Warning);
    }

    /// <summary>
    /// What the node accepts besides bare digits: spaces, tabs and line ends around the number and a leading +.
    /// Scenario files may contain these, so they stay; DropConnection and WaitForCommunication take no sign.
    /// </summary>
    [Theory]
    [InlineData(" 30 ", 30)]
    [InlineData("\t30\r\n", 30)]
    [InlineData("+5", 5)]
    [InlineData("007", 7)]
    public async Task Spaces_around_the_time_and_a_leading_plus_are_accepted(string value, int milliseconds)
    {
        using var simulator = new Side(ConnectionMode.Passive, Wait.GetFreePort());
        var wait = Scenario.Node(NodeType.Wait, value);

        var result = await simulator.Engine.ExecuteAsync(Scenario.Of(wait), deadline: RunDeadline).WaitAsync(RunDeadline);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains((LogLevel.Information, $"Waiting {milliseconds}ms..."), simulator.EngineLog.Entries);
        Assert.DoesNotContain(simulator.EngineLog.Entries, entry => entry.Level == LogLevel.Warning);
    }

    /// <summary>
    /// The time is read the same way on every computer. The culture of the computer decides which characters are the
    /// signs: with ICU data (Linux) the plus sign of Persian or Hebrew has a direction mark in front (U+200E U+002B),
    /// and Windows takes the signs of the user's own culture from its regional settings. A culture-dependent read
    /// does not take "+5" as a number on such a computer. The culture is made by hand, so the test does not depend
    /// on the culture data of the computer it runs on.
    /// </summary>
    [Fact]
    public async Task Time_is_read_the_same_in_every_culture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.PositiveSign = "\u200E+";
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            // The culture is in effect: a read without a culture does not take "+5" as a number.
            Assert.False(int.TryParse("+5", out _));
            using var simulator = new Side(ConnectionMode.Passive, Wait.GetFreePort());
            var wait = Scenario.Node(NodeType.Wait, "+5");

            var result = await simulator.Engine.ExecuteAsync(Scenario.Of(wait), deadline: RunDeadline).WaitAsync(RunDeadline);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains((LogLevel.Information, "Waiting 5ms..."), simulator.EngineLog.Entries);
            Assert.DoesNotContain(simulator.EngineLog.Entries, entry => entry.Level == LogLevel.Warning);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>
    /// The largest time, 2147483647 ms (24.8 days), is accepted: the node waits until the run is cancelled. Without
    /// a clock: the test goes on when the node says that it waits.
    /// </summary>
    [Fact]
    public async Task Largest_time_is_accepted()
    {
        using var simulator = new Side(ConnectionMode.Passive, Wait.GetFreePort());
        var wait = Scenario.Node(NodeType.Wait, "2147483647");
        using var cancellation = new CancellationTokenSource();

        var run = simulator.Engine.ExecuteAsync(Scenario.Of(wait), cancellation.Token);
        await Wait.UntilAsync(() => simulator.EngineLog.Messages.Contains("Waiting 2147483647ms..."),
            () => string.Join(" | ", simulator.EngineLog.Messages));
        Assert.False(run.IsCompleted);
        cancellation.Cancel();
        var result = await run.WaitAsync(RunDeadline);

        Assert.False(result.Success);
        Assert.StartsWith("Cancelled", result.ErrorMessage);
        Assert.DoesNotContain(simulator.EngineLog.Messages, message => message.StartsWith("Wait node "));
    }

    [Theory]
    [InlineData("20 s")]
    [InlineData("12,6")]
    [InlineData("12.600")]
    [InlineData("-5")]
    [InlineData("2147483648")]
    [InlineData("99999999999")]
    [InlineData("1 500")]
    [InlineData("\u00A0300")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Value_that_is_not_a_time_does_not_wait_and_logs_a_warning(string? value)
    {
        using var simulator = new Side(ConnectionMode.Passive, Wait.GetFreePort());
        var wait = Scenario.Node(NodeType.Wait, value);

        var result = await simulator.Engine.ExecuteAsync(Scenario.Of(wait), deadline: RunDeadline).WaitAsync(RunDeadline);

        Assert.True(result.Success, result.ErrorMessage);
        var warning = Assert.Single(simulator.EngineLog.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(
            $"Wait node {wait.Id}: '{value}' is not a time in ms (a whole number from 0 to 2147483647), the node does not wait",
            warning.Message);
        Assert.DoesNotContain(simulator.EngineLog.Messages, message => message.StartsWith("Waiting "));
    }

    /// <summary>0 is a readable time, however it is written: no wait and no warning.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("000")]
    [InlineData("+0")]
    [InlineData("-0")]
    public async Task Zero_does_not_wait_and_does_not_warn(string value)
    {
        using var simulator = new Side(ConnectionMode.Passive, Wait.GetFreePort());
        var wait = Scenario.Node(NodeType.Wait, value);

        var result = await simulator.Engine.ExecuteAsync(Scenario.Of(wait), deadline: RunDeadline).WaitAsync(RunDeadline);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain(simulator.EngineLog.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.DoesNotContain(simulator.EngineLog.Messages, message => message.StartsWith("Waiting "));
    }
}
