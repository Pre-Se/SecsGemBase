using System.Net;
using System.Net.Sockets;
using Logging.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemHelperClasses;
using SecsGemHelperClasses.Interfaces;
using SecsGemMessageHandling.Data_Handling;
using SecsGemMessageHandling.Enums;
using SecsGemMessageHandling.Helpers;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.Network;
using TCPIPBaseLibrary.TCPBase;
using Xunit;

namespace SecsGemMessageHandling.Tests;

/// <summary>
/// OnConnect must not run under CommunicationHandler's connection lock, a late OnConnect of a replaced connection must
/// be ignored, and a failed send restarts the connection through the queue instead of inline
/// (GitLab secsgem/secsgembase#4, #51 and #6).
/// </summary>
public class CommunicationHandlerOnConnectTests
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    /// <summary>
    /// The Select.req that ControlMessageHandling sends from OnConnect fails synchronously. Before the fix the failed
    /// send called RestartConnection() on the thread that held the connection lock inside OnConnected, so the lock
    /// stayed taken for good and ClosePort() never returned (#4).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_select_request_from_OnConnect_does_not_deadlock(bool sendThrows)
    {
        var factory = new FakeConnectionFactory(index => new FakeConnection
        {
            RaiseOnConnect = index == 0,
            SendThrows = sendThrows
        });
        using var stack = new Stack(factory, initiateSelect: true);

        stack.Communication.OpenPort();

        // The failed Select.req must lead to a restart, i.e. a second connection object.
        await WaitUntilAsync(() => factory.Created.Count == 2, () => stack.Describe());
        await Task.Run(stack.Communication.ClosePort).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stack.Communication.ConnectionOn);
        Assert.Equal(ConnectionStatus.PortClosed, stack.Communication.ConnectionStatus);
    }

    /// <summary>
    /// A connection whose loop has already read the OnConnect delegate reports its connect only after
    /// RestartConnection() replaced it (#51). That late report must not set Connected for the new, unconnected
    /// connection, and with InitiateSelectRequest its Select.req (sent over the new connection, which fails) must not
    /// block the connection lock. Here the report arrives while the restart is still disposing the connection, so it
    /// must stop waiting for the lock at once: the restart holds the lock while Dispose() waits for that very loop
    /// (1 s in the fake, 5 s in the real classes). A report that arrives after the restart completed is covered by
    /// Late_OnConnect_after_the_restart_completed_is_ignored.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_OnConnect_of_a_replaced_connection_is_ignored(bool initiateSelect)
    {
        var factory = new FakeConnectionFactory(index => new FakeConnection { RaiseOnConnect = index == 0, HoldOnConnect = true });
        using var stack = new Stack(factory, initiateSelect);

        stack.Communication.OpenPort();
        await WaitUntilAsync(() => factory.Created.Count == 1, () => stack.Describe());
        var replaced = factory.Created[0];
        await replaced.OnConnectRead.WaitAsync(TimeSpan.FromSeconds(5));

        // RestartConnection holds the lock while Dispose() waits for the loop that is about to report its connect.
        var restart = Task.Run(stack.Communication.RestartConnection);
        await replaced.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(5));
        replaced.ReleaseOnConnect();
        // Well below the fake's 1-s Dispose timeout: the late OnConnected must not keep the loop (and thus Dispose)
        // waiting for the lock that the restart holds.
        await restart.WaitAsync(TimeSpan.FromMilliseconds(700));
        await replaced.OnConnectReturned.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(ConnectionStatus.PortOpen, stack.Communication.ConnectionStatus);
        Assert.Equal(HSMSStatus.NotConnected, stack.Status);
        await Task.Run(stack.Communication.ClosePort).WaitAsync(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// When RestartConnection() throws in the queued restart, later restarts must still run. Before the fix the
    /// pending flag stayed set and every further automatic restart was skipped without a trace (#4, #6).
    /// </summary>
    [Fact]
    public async Task Queued_restart_still_works_after_a_restart_threw()
    {
        var factory = new FakeConnectionFactory(index => new FakeConnection { DisposeThrowsOnce = index == 0 });
        using var stack = new Stack(factory, initiateSelect: false);
        var request = new ControlMessageFactory(new HSMSParameters()).CreateSelectRequest();

        stack.Communication.OpenPort();
        await WaitUntilAsync(() => factory.Created.Count == 1, () => stack.Describe());

        // First failed send: the restart throws in Dispose() of the old connection.
        var first = await stack.Communication.SendStateMessage(request);
        Assert.Equal(MessageStatus.Failure, first.Status);
        await WaitUntilAsync(() => factory.Created[0].DisposeCalls == 1, () => stack.Describe());
        await Task.Delay(100);

        // Second failed send: this restart must still run and replace the connection.
        await stack.Communication.SendStateMessage(request);
        await WaitUntilAsync(() => factory.Created.Count == 2, () => stack.Describe());
        Assert.True(stack.Communication.ConnectionOn);
    }

    /// <summary>
    /// Active side against a remote that accepts every connection and resets it at once: the Select.req from OnConnect
    /// fails. Before the fixes the connection lock either stayed taken (#4) or the client loop threw
    /// InvalidOperationException from GetStream() and the port was closed for good (#6). A late OnConnect of a
    /// replaced connection (until #5 its loop reconnected by itself) must not stall a restart until Dispose() times
    /// out (#51); since #5 this test no longer provokes that, see Late_OnConnect_after_the_restart_completed_is_ignored.
    /// </summary>
    [Fact]
    public async Task Remote_that_resets_every_connection_neither_hangs_nor_closes_the_port()
    {
        using var remote = new ResettingRemote();
        var logger = new RecordingLogger<CommunicationHandler>();
        var connectionLogger = new RecordingLogger<TCPIPClientBase>();
        using var stack = new Stack(new TcpConnectionFactory(remote.Port, connectionLogger), initiateSelect: true, logger);

        stack.Communication.OpenPort();
        await WaitUntilAsync(() => remote.Accepted >= 5, () => stack.Describe() + $", accepted={remote.Accepted}");
        await Task.Delay(1000);

        Assert.True(stack.Communication.ConnectionOn, stack.Describe());
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Unhandled exception"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Restarting the connection failed"));
        Assert.DoesNotContain(connectionLogger.Messages, m => m.Contains("did not stop within timeout"));
        await Task.Run(stack.Communication.ClosePort).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConnectionStatus.PortClosed, stack.Communication.ConnectionStatus);
    }

    /// <summary>
    /// ITCPIPBase does not require the event's sender to be the object the factory returned; a decorator (WaffleMaker's
    /// SafeTcp) passes on the sender of the connection it wraps. Such a connect must still count.
    /// </summary>
    [Fact]
    public async Task Connect_reported_with_another_sender_is_accepted()
    {
        var factory = new FakeConnectionFactory(index => new FakeConnection { RaiseOnConnect = index == 0, ForeignSender = true });
        using var stack = new Stack(factory, initiateSelect: false);

        stack.Communication.OpenPort();

        await WaitUntilAsync(() => stack.Communication.ConnectionStatus == ConnectionStatus.Connected
                                   && stack.Status == HSMSStatus.NotSelected, () => stack.Describe());
    }

    /// <summary>
    /// Host (passive) and equipment (active) over loopback, both behind a decorator that passes on the inner sender:
    /// both must reach Selected.
    /// </summary>
    [Fact]
    public async Task Decorated_connections_reach_selected()
    {
        var port = GetFreePort();
        using var host = new Stack(new DecoratingTcpFactory(port), initiateSelect: false, mode: ConnectionMode.Passive);
        using var equipment = new Stack(new DecoratingTcpFactory(port), initiateSelect: true, mode: ConnectionMode.Active);

        host.Communication.OpenPort();
        equipment.Communication.OpenPort();

        await WaitUntilAsync(() => host.Selected && equipment.Selected,
            () => $"host: {host.Describe()}; equipment: {equipment.Describe()}");
    }

    /// <summary>
    /// The core #51 case: the late report arrives only after RestartConnection() has completed (here because Dispose()
    /// did not wait for the loop). The successor's token is not cancelled, so only the check that the reporting
    /// connection is still the current one drops the report. The successor's own connect must then select normally.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_OnConnect_after_the_restart_completed_is_ignored(bool initiateSelect)
    {
        var factory = new FakeConnectionFactory(index => new FakeConnection
        {
            RaiseOnConnect = true,
            HoldOnConnect = true,
            DisposeWaits = index != 0,
            SendSucceeds = true
        });
        using var stack = new Stack(factory, initiateSelect);

        stack.Communication.OpenPort();
        await WaitUntilAsync(() => factory.Created.Count == 1, () => stack.Describe());
        var replaced = factory.Created[0];
        await replaced.OnConnectRead.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(stack.Communication.RestartConnection).WaitAsync(TimeSpan.FromSeconds(5));
        var current = factory.Created[1];

        replaced.ReleaseOnConnect();
        await replaced.OnConnectReturned.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        Assert.Equal(ConnectionStatus.PortOpen, stack.Communication.ConnectionStatus);
        Assert.Equal(HSMSStatus.NotConnected, stack.Status);
        Assert.Equal(0, current.SendCalls);

        current.ReleaseOnConnect();
        await WaitUntilAsync(() => stack.Communication.ConnectionStatus == ConnectionStatus.Connected
                                   && stack.Status == HSMSStatus.NotSelected
                                   && current.SendCalls == (initiateSelect ? 1 : 0), () => stack.Describe());
    }

    /// <summary>
    /// A restart that overlaps the OnConnect handlers: an earlier subscriber restarts inline, and Dispose() does not
    /// wait for the loop. ControlMessageHandling's handler then runs for a connection that is already gone. It must
    /// not leave NotSelected behind (T7 running, Select.req over the unconnected successor), or the successor's own
    /// connect would find NotSelected and send no Select.req.
    /// </summary>
    [Fact]
    public async Task Restart_overlapping_the_OnConnect_handlers_does_not_leave_NotSelected()
    {
        var factory = new FakeConnectionFactory(index => new FakeConnection
        {
            RaiseOnConnect = true,
            HoldOnConnect = index != 0,
            DisposeWaits = false,
            SendSucceeds = true
        });
        var once = 0;
        using var stack = new Stack(factory, initiateSelect: true, beforeControl: communication =>
            communication.OnConnect += (_, _) =>
            {
                if (Interlocked.Exchange(ref once, 1) == 0) communication.RestartConnection();
            });

        stack.Communication.OpenPort();
        await WaitUntilAsync(() => factory.Created.Count == 2, () => stack.Describe());
        var current = factory.Created[1];
        await factory.Created[0].OnConnectReturned.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        Assert.Equal(HSMSStatus.NotConnected, stack.Status);
        Assert.Equal(0, current.SendCalls);

        current.ReleaseOnConnect();
        await WaitUntilAsync(() => stack.Status == HSMSStatus.NotSelected && current.SendCalls == 1, () => stack.Describe());
    }

    /// <summary>
    /// ClosePort() while the OnConnect handlers still run on the connection's loop: OnDisconnect must not overtake
    /// ControlMessageHandling's NotSelected, or HSMS stays NotSelected (and after T7 CommunicationFailure) with the
    /// port closed. Before the fix ClosePort set the status, and so raised OnDisconnect, before disposing.
    /// </summary>
    [Fact]
    public async Task ClosePort_during_OnConnect_ends_in_NotConnected()
    {
        var factory = new FakeConnectionFactory(index => new FakeConnection { RaiseOnConnect = index == 0, SendSucceeds = true });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stack = new Stack(factory, initiateSelect: false, beforeControl: communication =>
            communication.OnConnect += (_, _) =>
            {
                entered.TrySetResult();
                Thread.Sleep(300);
            });

        stack.Communication.OpenPort();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(stack.Communication.ClosePort).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ConnectionStatus.PortClosed, stack.Communication.ConnectionStatus);
        await Task.Delay(200);
        Assert.Equal(HSMSStatus.NotConnected, stack.Status);
    }

    /// <summary>
    /// The fourth trigger in #4: an OnConnect handler that closes or restarts the connection itself. Under the lock
    /// that was a permanent deadlock; now it returns once Dispose() stopped waiting for the handler's own loop.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnConnect_handler_that_closes_or_restarts_returns(bool close)
    {
        var factory = new FakeConnectionFactory(index => new FakeConnection { RaiseOnConnect = index == 0 });
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = 0;
        using var stack = new Stack(factory, initiateSelect: false, beforeControl: communication =>
            communication.OnConnect += (_, _) =>
            {
                if (Interlocked.Exchange(ref once, 1) != 0) return;
                if (close) communication.ClosePort();
                else communication.RestartConnection();
                returned.TrySetResult();
            });

        stack.Communication.OpenPort();

        // The fake's Dispose() waits 1 s for the loop the handler runs on.
        await returned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(close ? ConnectionStatus.PortClosed : ConnectionStatus.PortOpen, stack.Communication.ConnectionStatus);
        Assert.Equal(2, factory.Created.Count);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Condition was not met within 5 s ({state()})");
            await Task.Delay(10);
        }
    }

    /// <summary>CommunicationHandler + TransactionHandler + ControlMessageHandling, wired like the DI registration.</summary>
    private sealed class Stack : IDisposable
    {
        public CommunicationHandler Communication { get; }
        private ControlMessageHandling Control { get; }
        public HSMSStatus Status => Control.HSMSStatus;

        public Stack(INetworkConnectionFactory factory, bool initiateSelect, ILogger<CommunicationHandler>? logger = null,
            ConnectionMode mode = ConnectionMode.Active, Action<CommunicationHandler>? beforeControl = null)
        {
            var settings = new NetworkSettings { IpAddress = IPAddress.Loopback.ToString(), ConnectionMode = mode };
            var parameters = new HSMSParameters { T5 = 100, InitiateSelectRequest = initiateSelect };
            var controlMessages = new ControlMessageFactory(parameters);
            Communication = new CommunicationHandler(settings, factory, logger ?? NullLogger<CommunicationHandler>.Instance,
                parameters, controlMessages, EventBuses);
            // Lets a test subscribe to OnConnect/OnDisconnect before ControlMessageHandling does.
            beforeControl?.Invoke(Communication);
            var transactions = new TransactionHandler(parameters, NullLogger<TransactionHandler>.Instance, Communication, EventBuses);
            Control = new ControlMessageHandling(Communication, NullLogger<ControlMessageHandling>.Instance, parameters,
                controlMessages, transactions);
        }

        public bool Selected => Status == HSMSStatus.Selected;

        public string Describe() =>
            $"ConnectionOn={Communication.ConnectionOn}, ConnectionStatus={Communication.ConnectionStatus}, HSMSStatus={Status}";

        // Not ClosePort(): after a deadlock it would block the test run.
        public void Dispose() => _ = Task.Run(() =>
        {
            Communication.ClosePort();
            Communication.Dispose();
        });
    }

    private sealed class FakeConnectionFactory(Func<int, FakeConnection> create) : INetworkConnectionFactory
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
                var connection = create(created.Count);
                created.Add(connection);
                return connection;
            }
        }
    }

    /// <summary>
    /// Behaves like the real TCP classes where it matters here: Connect() raises OnConnect synchronously from its own
    /// loop (reading the delegate first), SendData fails without a connection, and Dispose() waits a while for the loop.
    /// </summary>
    private sealed class FakeConnection : ITCPIPBase
    {
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource onConnectRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource onConnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource onConnectReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EventHandler? onConnect;
        private Task? loop;
        private int disposeCalls;

        private int sendCalls;

        public bool RaiseOnConnect { get; init; }
        public bool HoldOnConnect { get; init; }
        public bool SendThrows { get; init; }
        public bool SendSucceeds { get; init; }
        public bool DisposeThrowsOnce { get; init; }

        /// <summary>Raise OnConnect with another object as sender, like a decorator that passes on its inner sender.</summary>
        public bool ForeignSender { get; init; }

        /// <summary>
        /// false models the real classes when the first ConnectAsync/Accept completed synchronously: their
        /// receiveDataLoopTask is not assigned yet, so Dispose() returns without waiting for the loop.
        /// </summary>
        public bool DisposeWaits { get; init; } = true;

        public int SendCalls => Volatile.Read(ref sendCalls);

        public event EventHandler? OnConnect
        {
            add => onConnect += value;
            remove => onConnect -= value;
        }

        public event EventHandler? OnDisconnected { add { } remove { } }
        public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; } = EventBuses.CreateTransient<ReadOnlyMemory<byte>>();
        public Task OnConnectRead => onConnectRead.Task;
        public Task OnConnectReturned => onConnectReturned.Task;
        public Task DisposeStarted => disposeStarted.Task;
        public int DisposeCalls => Volatile.Read(ref disposeCalls);

        public void ReleaseOnConnect() => onConnectGate.TrySetResult();

        public Task Connect() => loop = Loop();

        private async Task Loop()
        {
            await Task.Yield();
            if (RaiseOnConnect)
            {
                // Like `OnConnect?.Invoke(...)`: the delegate is read before it is called.
                var handler = onConnect;
                onConnectRead.TrySetResult();
                if (HoldOnConnect) await onConnectGate.Task;
                handler?.Invoke(ForeignSender ? new object() : this, EventArgs.Empty);
                onConnectReturned.TrySetResult();
            }

            await stopped.Task;
        }

        public Task<bool> SendData(ReadOnlyMemory<byte> data)
        {
            Interlocked.Increment(ref sendCalls);
            return SendThrows ? throw new IOException("Broken pipe") : Task.FromResult(SendSucceeds);
        }

        public void Dispose()
        {
            var calls = Interlocked.Increment(ref disposeCalls);
            disposeStarted.TrySetResult();
            stopped.TrySetResult();
            try { if (DisposeWaits) loop?.Wait(TimeSpan.FromSeconds(1)); }
            catch (AggregateException) { }
            if (DisposeThrowsOnce && calls == 1) throw new InvalidOperationException("Dispose failed");
        }
    }

    /// <summary>Creates real TCP connections behind a decorator that passes on the inner sender (like SafeTcp).</summary>
    private sealed class DecoratingTcpFactory(int port) : INetworkConnectionFactory
    {
        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode)
        {
            var settings = new NetworkSettings
            {
                IpAddress = IPAddress.Loopback.ToString(),
                Port = (ushort)port,
                ConnectionMode = connectionMode
            };
            ITCPIPBase inner = connectionMode == ConnectionMode.Active
                ? new TCPIPClientBase(settings, NullLogger<TCPIPClientBase>.Instance, EventBuses) { ConnectSeparationTimeout = 100 }
                : new TCPIPServerBase(settings, NullLogger<TCPIPServerBase>.Instance, EventBuses);
            return new Decorator(inner);
        }
    }

    private sealed class Decorator : ITCPIPBase
    {
        private readonly ITCPIPBase inner;
        private EventHandler? onConnect;
        private EventHandler? onDisconnected;

        public Decorator(ITCPIPBase inner)
        {
            this.inner = inner;
            inner.OnConnect += (sender, e) => onConnect?.Invoke(sender, e);
            inner.OnDisconnected += (sender, e) => onDisconnected?.Invoke(sender, e);
        }

        public event EventHandler? OnConnect
        {
            add => onConnect += value;
            remove => onConnect -= value;
        }

        public event EventHandler? OnDisconnected
        {
            add => onDisconnected += value;
            remove => onDisconnected -= value;
        }

        public IEventBus<ReadOnlyMemory<byte>> OnDataReceived => inner.OnDataReceived;
        public Task Connect() => inner.Connect();
        public Task<bool> SendData(ReadOnlyMemory<byte> data) => inner.SendData(data);
        public void Dispose() => inner.Dispose();
    }

    private sealed class TcpConnectionFactory(int port, ILogger<TCPIPClientBase> logger) : INetworkConnectionFactory
    {
        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode) =>
            new TCPIPClientBase(new NetworkSettings
            {
                IpAddress = IPAddress.Loopback.ToString(),
                Port = (ushort)port,
                ConnectionMode = ConnectionMode.Active
            }, logger, EventBuses);
    }

    /// <summary>Accepts every connection and resets it at once (RST, not FIN).</summary>
    private sealed class ResettingRemote : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private int accepted;

        public int Port { get; }
        public int Accepted => Volatile.Read(ref accepted);

        public ResettingRemote()
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var socket = await listener.AcceptSocketAsync(stop.Token);
                    Interlocked.Increment(ref accepted);
                    // Close(0) without a prior Shutdown sends RST.
                    socket.Close(0);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (messages) return messages.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages) messages.Add(formatter(state, exception));
        }
    }
}
