using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemHelperClasses;
using SecsGemHelperClasses.Interfaces;
using SecsGemMessageHandling.Data_Handling;
using SecsGemMessageHandling.Enums;
using SecsGemMessageHandling.Helpers;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.Network;
using Xunit;

namespace SecsGemMessageHandling.Tests;

/// <summary>
/// CommunicationHandler.TryDropConnection, used by the scenario node DropConnection (GitLab secsgem/secsgembase#54).
/// The loopback tests of the node are in SecsGemScenarioEngine.Tests.
/// </summary>
public class TryDropConnectionTests
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    /// <summary>
    /// After the connection has reported its end, ConnectionStatus stays Connected until the queued restart runs. A drop
    /// in that moment would abort a connection that is already gone (no reset) and replace the connection that the
    /// queued restart then replaces again; it changes nothing instead. The drop runs in an OnDisconnected handler right
    /// after the CommunicationHandler's own, so it usually takes the lock before the queued restart; either way it must
    /// return false.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Drop_changes_nothing_once_the_connection_has_reported_its_end(bool closePort)
    {
        var factory = new FakeConnectionFactory();
        using var communication = new CommunicationHandler(Settings(), factory,
            NullLogger<CommunicationHandler>.Instance, new HSMSParameters(),
            new ControlMessageFactory(new HSMSParameters()), EventBuses);
        communication.OpenPort();
        await WaitUntilAsync(() => communication.ConnectionStatus == ConnectionStatus.Connected);
        var first = factory.Created[0];
        bool? dropped = null;
        first.OnDisconnected += (_, _) => dropped = communication.TryDropConnection(closePort, CancellationToken.None, out _);

        first.RaiseOnDisconnected();

        Assert.False(dropped);
        Assert.False(first.Aborted);
        // The queued restart replaces the connection; the port stays open.
        await WaitUntilAsync(() => factory.Created.Count == 2);
        Assert.True(communication.ConnectionOn);
    }

    /// <summary>
    /// A run stopped while the drop waits for the connection lock (here held by a restart whose old connection takes long
    /// to dispose) drops nothing: the wait ends with an OperationCanceledException.
    /// </summary>
    [Fact]
    public async Task Cancelled_while_waiting_for_the_lock_drops_nothing()
    {
        using var gate = new ManualResetEventSlim();
        var factory = new FakeConnectionFactory(gate);
        using var communication = new CommunicationHandler(Settings(), factory,
            NullLogger<CommunicationHandler>.Instance, new HSMSParameters(),
            new ControlMessageFactory(new HSMSParameters()), EventBuses);
        communication.OpenPort();
        await WaitUntilAsync(() => communication.ConnectionStatus == ConnectionStatus.Connected);
        var first = factory.Created[0];
        try
        {
            var restart = Task.Run(communication.RestartConnection);
            await WaitUntilAsync(() => first.DisposeStarted);
            using var cancellation = new CancellationTokenSource();
            var drop = Task.Run(() => communication.TryDropConnection(closePort: true, cancellation.Token, out _));
            await Task.Delay(100);
            Assert.False(drop.IsCompleted);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drop.WaitAsync(TimeSpan.FromSeconds(5)));
            gate.Set();
            await restart.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(first.Aborted);
            Assert.True(communication.ConnectionOn);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public void Drop_without_a_connection_changes_nothing()
    {
        var factory = new FakeConnectionFactory();
        using var communication = new CommunicationHandler(Settings(), factory,
            NullLogger<CommunicationHandler>.Instance, new HSMSParameters(),
            new ControlMessageFactory(new HSMSParameters()), EventBuses);

        Assert.False(communication.TryDropConnection(closePort: true, CancellationToken.None, out _));
        Assert.Single(factory.Created);
        Assert.Equal(ConnectionStatus.PortClosed, communication.ConnectionStatus);
    }

    private static INetworkSettings Settings() => new NetworkSettings
    {
        IpAddress = IPAddress.Loopback.ToString(),
        Port = 5000,
        ConnectionMode = ConnectionMode.Active
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time");
            await Task.Delay(5);
        }
    }

    /// <summary>The first connection connects at once; later ones never do.</summary>
    /// <param name="disposeGate">If set, Dispose() of the first connection waits for it.</param>
    private sealed class FakeConnectionFactory(ManualResetEventSlim? disposeGate = null) : INetworkConnectionFactory
    {
        private readonly List<FakeConnection> created = [];

        public IReadOnlyList<FakeConnection> Created
        {
            get { lock (created) return created.ToList(); }
        }

        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode)
        {
            lock (created)
            {
                var connection = created.Count == 0
                    ? new FakeConnection(connects: true, disposeGate)
                    : new FakeConnection(connects: false, disposeGate: null);
                created.Add(connection);
                return connection;
            }
        }
    }

    private sealed class FakeConnection(bool connects, ManualResetEventSlim? disposeGate) : ITCPIPBase
    {
        private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int aborted;
        private int disposeStarted;

        public event EventHandler? OnConnect;
        public event EventHandler? OnDisconnected;
        public bool Aborted => Volatile.Read(ref aborted) == 1;
        public bool DisposeStarted => Volatile.Read(ref disposeStarted) == 1;
        public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; } = EventBuses.CreateTransient<ReadOnlyMemory<byte>>();

        public void RaiseOnDisconnected() => OnDisconnected?.Invoke(this, EventArgs.Empty);

        public async Task Connect()
        {
            if (connects) OnConnect?.Invoke(this, EventArgs.Empty);
            await ended.Task;
        }

        public Task<bool> SendData(ReadOnlyMemory<byte> data) => Task.FromResult(false);

        public void Abort()
        {
            Volatile.Write(ref aborted, 1);
            Dispose();
        }

        public void Dispose()
        {
            Volatile.Write(ref disposeStarted, 1);
            disposeGate?.Wait(TimeSpan.FromSeconds(10));
            ended.TrySetResult();
        }
    }
}
