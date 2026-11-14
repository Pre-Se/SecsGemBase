using System.Net;
using System.Net.Sockets;
using Logging.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemBaseItems.SecsGemParameters.Enums;
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
/// DataMessageHandler.WaitForReceivedMessage (the scenario engine's Receive node) also looks 5 s back into a buffer of
/// received messages. A message that arrived before the connection was lost must not satisfy a wait that starts after
/// the reconnect (GitLab secsgem/secsgembase#55): "after reconnecting the equipment must send event Y again" passed
/// although nothing was sent again.
/// </summary>
public class ReceiveAfterReconnectTests
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);

    /// <summary>The buffered message of the replaced connection is skipped; one of the new connection is taken.</summary>
    [Fact]
    public async Task Wait_after_a_restart_skips_the_buffered_message_of_the_old_connection()
    {
        var factory = new FakeConnectionFactory();
        using var stack = new Stack(factory);
        var old = factory.Created[0];

        // Wait first, then deliver: a message received just before a wait can be missed (GitLab #52).
        var first = stack.Data.WaitForReceivedMessage(IsEventReport, Long);
        old.Deliver(stack.Frame(1));
        Assert.Equal(1u, (await first)?.HeaderData.SystemBytes);

        stack.Communication.RestartConnection();
        Assert.Equal(2, factory.Created.Count);

        Assert.Null(await stack.Data.WaitForReceivedMessage(IsEventReport, TimeSpan.FromMilliseconds(300)));

        var waiting = stack.Data.WaitForReceivedMessage(IsEventReport, Long);
        factory.Created[1].Deliver(stack.Frame(2));
        Assert.Equal(2u, (await waiting)?.HeaderData.SystemBytes);
    }

    /// <summary>
    /// The old connection's OnDataReceived bus may deliver a message after the new connection is up, so it is parsed
    /// late. A wait that started after the restart does not take it, neither from the buffer nor live, and does not
    /// offer it to its accept callback (which claims messages in the scenario engine). A wait that was already running
    /// on the old connection still takes it: what counts is the connection current when the wait started.
    /// </summary>
    [Fact]
    public async Task Message_of_the_old_connection_parsed_late_does_not_satisfy_a_new_wait()
    {
        var factory = new FakeConnectionFactory();
        using var stack = new Stack(factory);
        var old = factory.Created[0];
        var acrossRestart = stack.Data.WaitForReceivedMessage(IsEventReport, Long);

        stack.Communication.RestartConnection();
        var offered = new List<uint>();
        var afterRestart = stack.Data.WaitForReceivedMessage(IsEventReport, Long, accept: m =>
        {
            lock (offered) offered.Add(m.HeaderData.SystemBytes);
            return true;
        });
        old.Deliver(stack.Frame(1));
        Assert.Equal(1u, (await acrossRestart)?.HeaderData.SystemBytes);
        await Task.Delay(300);
        if (afterRestart.IsCompleted)
            Assert.Fail($"Took S6F11 {(await afterRestart)?.HeaderData.SystemBytes} of the old connection");

        factory.Created[1].Deliver(stack.Frame(2));
        Assert.Equal(2u, (await afterRestart)?.HeaderData.SystemBytes);
        lock (offered) Assert.Equal([2u], offered);
    }

    /// <summary>
    /// Between the moment a connection reports its end and the moment its restart has built the next one, a new wait
    /// already skips the lost connection's messages. The restart is held in Dispose() of the lost connection here.
    /// </summary>
    [Fact]
    public async Task Wait_after_the_loss_is_reported_skips_the_old_message_before_the_restart_has_run()
    {
        using var gate = new ManualResetEventSlim();
        var factory = new FakeConnectionFactory(disposeGate: gate);
        using var stack = new Stack(factory);
        var lost = factory.Created[0];
        var first = stack.Data.WaitForReceivedMessage(IsEventReport, Long);
        lost.Deliver(stack.Frame(1));
        Assert.Equal(1u, (await first)?.HeaderData.SystemBytes);

        lost.RaiseOnDisconnected();
        try
        {
            await WaitUntilAsync(() => lost.DisposeStarted, () => "the restart did not start");
            Assert.Single(factory.Created);
            Assert.Null(await stack.Data.WaitForReceivedMessage(IsEventReport, TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            gate.Set();
        }

        // Until the restart has subscribed to the new connection, a frame delivered there would be lost.
        await WaitUntilAsync(() => factory.Created.Count == 2 && factory.Created[1].Subscribed,
            () => $"created={factory.Created.Count}");
        var next = stack.Data.WaitForReceivedMessage(IsEventReport, Long);
        factory.Created[1].Deliver(stack.Frame(2));
        Assert.Equal(2u, (await next)?.HeaderData.SystemBytes);
    }

    /// <summary>
    /// The ticket's case with real sockets: the remote side sends S6F11, the connection is reset and comes back, and a
    /// wait started then (well within 5 s of the S6F11) waits until the remote side sends it again.
    /// </summary>
    [Fact]
    public async Task Event_sent_before_a_reset_does_not_satisfy_a_wait_after_the_reconnect()
    {
        using var remote = new Remote();
        using var stack = new Stack(new ClientFactory(remote.Port), remote.Port);
        stack.Communication.OpenPort();
        try
        {
            var first = await remote.AcceptAsync(stack);
            var received = stack.Data.WaitForReceivedMessage(IsEventReport, Long);
            first.Send(stack.Frame(1));
            Assert.Equal(1u, (await received)?.HeaderData.SystemBytes);

            first.Close(0); // RST
            var second = await remote.AcceptAsync(stack);

            Assert.Null(await stack.Data.WaitForReceivedMessage(IsEventReport, TimeSpan.FromMilliseconds(500)));
            var resent = stack.Data.WaitForReceivedMessage(IsEventReport, Long);
            second.Send(stack.Frame(2));
            Assert.Equal(2u, (await resent)?.HeaderData.SystemBytes);
        }
        finally
        {
            stack.Communication.ClosePort(); // before Dispose(), GitLab secsgem/secsgembase#62
        }
    }

    /// <summary>
    /// The remote side sends S6F11 and closes the connection right away, so the message may still be in flight (on the
    /// old connection's bus) while the client reconnects. Repeated, because that race does not hit every time.
    /// </summary>
    [Fact]
    public async Task Event_sent_right_before_the_remote_closes_does_not_satisfy_a_wait_after_the_reconnect()
    {
        using var remote = new Remote();
        using var stack = new Stack(new ClientFactory(remote.Port), remote.Port);
        stack.Communication.OpenPort();
        try
        {
            var socket = await remote.AcceptAsync(stack);
            for (uint round = 1; round <= 10; round++)
            {
                socket.Send(stack.Frame(round));
                socket.Shutdown(SocketShutdown.Both); // FIN after the data, so the message is delivered
                socket.Close();
                socket = await remote.AcceptAsync(stack);

                var taken = await stack.Data.WaitForReceivedMessage(IsEventReport, TimeSpan.FromMilliseconds(200));
                Assert.True(taken is null, $"Round {round}: took S6F11 {taken?.HeaderData.SystemBytes} of a closed connection");
            }

            var last = stack.Data.WaitForReceivedMessage(IsEventReport, Long);
            socket.Send(stack.Frame(100));
            Assert.Equal(100u, (await last)?.HeaderData.SystemBytes);
            socket.Close(0);
        }
        finally
        {
            stack.Communication.ClosePort();
        }
    }

    private static bool IsEventReport(SecsGemDataMessage message) => message is { Stream: 6, Function: 11 };

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Condition was not met in time ({state()})");
            await Task.Delay(5);
        }
    }

    /// <summary>CommunicationHandler with the handlers DataMessageHandler needs; no Select.req, so no S1F13.</summary>
    private sealed class Stack : IDisposable
    {
        public CommunicationHandler Communication { get; }
        public DataMessageHandler Data { get; }

        public Stack(INetworkConnectionFactory factory, int port = 0)
        {
            var settings = new NetworkSettings
            {
                IpAddress = IPAddress.Loopback.ToString(),
                Port = (ushort)port,
                ConnectionMode = ConnectionMode.Active
            };
            var parameters = new HSMSParameters { T5 = 100 };
            var controlMessages = new ControlMessageFactory(parameters);
            Communication = new CommunicationHandler(settings, factory, NullLogger<CommunicationHandler>.Instance,
                parameters, controlMessages, EventBuses);
            var transactions = new TransactionHandler(parameters, NullLogger<TransactionHandler>.Instance,
                Communication, EventBuses);
            var control = new ControlMessageHandling(Communication, NullLogger<ControlMessageHandling>.Instance,
                parameters, controlMessages, transactions);
            Data = new DataMessageHandler(parameters, Communication, NullLogger<DataMessageHandler>.Instance,
                transactions, new ControlStateInfo(), control, EventBuses);
        }

        /// <summary>An S6F11 frame (length, header, body) as the remote side would send it.</summary>
        public byte[] Frame(uint systemBytes) =>
            Communication.BuildMessageData(new MessageFactory(6, 11, false, "Event Report Send").Build(), systemBytes)
                .rawData;

        public void Dispose() => Communication.Dispose();
    }

    /// <param name="disposeGate">If set, Dispose() of the first connection waits for it (holding the restart).</param>
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
                var connection = new FakeConnection(created.Count == 0 ? disposeGate : null);
                created.Add(connection);
                return connection;
            }
        }
    }

    /// <summary>
    /// Never connects; the test delivers received frames on its OnDataReceived bus, also after Dispose(), and raises
    /// OnDisconnected as after a lost connection.
    /// </summary>
    private sealed class FakeConnection(ManualResetEventSlim? disposeGate) : ITCPIPBase
    {
        private EventHandler? onDisconnected;
        public event EventHandler? OnConnect { add { } remove { } }

        public event EventHandler? OnDisconnected
        {
            add => onDisconnected += value;
            remove => onDisconnected -= value;
        }

        public void RaiseOnDisconnected() => onDisconnected?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// CommunicationHandler.BuildConnectionBase subscribes OnDisconnected last, after OnDataReceived: from then on a
        /// delivered frame is parsed.
        /// </summary>
        public bool Subscribed => Volatile.Read(ref onDisconnected) is not null;
        public bool DisposeStarted => Volatile.Read(ref disposeStarted);
        private bool disposeStarted;
        public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; } = EventBuses.CreateTransient<ReadOnlyMemory<byte>>();
        public void Deliver(byte[] frame) => OnDataReceived.Publish(frame);
        public Task Connect() => Task.Delay(Timeout.Infinite);
        public Task<bool> SendData(ReadOnlyMemory<byte> data) => Task.FromResult(false);

        // Like the library's TCP classes, Dispose() leaves the bus running (GitLab secsgem/secsgembase#7).
        public void Dispose()
        {
            Volatile.Write(ref disposeStarted, true);
            disposeGate?.Wait(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class ClientFactory(int port) : INetworkConnectionFactory
    {
        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode) => new TCPIPClientBase(
            new NetworkSettings
            {
                IpAddress = IPAddress.Loopback.ToString(),
                Port = (ushort)port,
                ConnectionMode = ConnectionMode.Active
            }, NullLogger<TCPIPClientBase>.Instance, EventBuses);
    }

    /// <summary>The remote side: accepts the client's connections on a port of its own and sends raw frames.</summary>
    private sealed class Remote : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);

        public int Port { get; }

        public Remote()
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>Accepts the next connection and waits until the client reports it as Connected.</summary>
        public async Task<Socket> AcceptAsync(Stack stack)
        {
            var socket = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => stack.Communication.ConnectionStatus == ConnectionStatus.Connected,
                () => $"{stack.Communication.ConnectionStatus}");
            return socket;
        }

        public void Dispose() => listener.Stop();
    }
}
