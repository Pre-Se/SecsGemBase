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
/// Covers how CommunicationHandler reacts to an exception out of ITCPIPBase.Connect() (GitLab secsgem/secsgembase#3)
/// without sockets or timing: the fake connections' Connect() tasks are faulted by the test.
/// </summary>
public class CommunicationHandlerConnectLoopTests
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    [Fact]
    public async Task Exception_of_a_replaced_connection_does_not_close_the_port()
    {
        var factory = new FakeConnectionFactory();
        using var handler = CreateHandler(factory);

        handler.OpenPort();
        var replaced = factory.Created[0];
        await replaced.ConnectCalled.WaitAsync(TimeSpan.FromSeconds(5));

        handler.RestartConnection();
        var current = factory.Created[1];
        await current.ConnectCalled.WaitAsync(TimeSpan.FromSeconds(5));

        // What TCPIPServerBase.Connect() threw before the fix when Dispose() won the race for the semaphore.
        replaced.FailConnect(new ObjectDisposedException("System.Threading.SemaphoreSlim"));
        await Task.Delay(200);

        Assert.True(handler.ConnectionOn);
        Assert.Equal(ConnectionStatus.PortOpen, handler.ConnectionStatus);
        Assert.False(current.Disposed);
        Assert.Equal(2, factory.Created.Count);
    }

    [Fact]
    public async Task Exception_of_the_current_connection_still_closes_the_port()
    {
        var factory = new FakeConnectionFactory();
        using var handler = CreateHandler(factory);

        handler.OpenPort();
        var current = factory.Created[0];
        await current.ConnectCalled.WaitAsync(TimeSpan.FromSeconds(5));

        current.FailConnect(new InvalidOperationException("connection failed"));

        await WaitUntilAsync(() => !handler.ConnectionOn);
        Assert.Equal(ConnectionStatus.PortClosed, handler.ConnectionStatus);
        Assert.True(current.Disposed);
        Assert.Equal(2, factory.Created.Count);
    }

    private static CommunicationHandler CreateHandler(INetworkConnectionFactory factory)
    {
        var settings = new NetworkSettings { ConnectionMode = ConnectionMode.Passive };
        var parameters = new HSMSParameters();
        return new CommunicationHandler(settings, factory, NullLogger<CommunicationHandler>.Instance, parameters,
            new ControlMessageFactory(parameters), EventBuses);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met within 5 s");
            await Task.Delay(10);
        }
    }

    private sealed class FakeConnectionFactory : INetworkConnectionFactory
    {
        private readonly List<FakeConnection> created = [];

        public IReadOnlyList<FakeConnection> Created
        {
            get { lock (created) return created.ToList(); }
        }

        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode)
        {
            var connection = new FakeConnection();
            lock (created) created.Add(connection);
            return connection;
        }
    }

    private sealed class FakeConnection : ITCPIPBase
    {
        private readonly TaskCompletionSource connectCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource connectLoop = new();

        // Never raised: these tests only drive the Connect() task.
        public event EventHandler? OnConnect { add { } remove { } }
        public event EventHandler? OnDisconnected { add { } remove { } }
        public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; } = EventBuses.CreateTransient<ReadOnlyMemory<byte>>();
        public Task ConnectCalled => connectCalled.Task;
        public bool Disposed { get; private set; }

        public Task Connect()
        {
            connectCalled.TrySetResult();
            return connectLoop.Task;
        }

        public void FailConnect(Exception exception) => connectLoop.SetException(exception);

        public Task<bool> SendData(ReadOnlyMemory<byte> data) => Task.FromResult(false);

        public void Dispose() => Disposed = true;
    }
}
