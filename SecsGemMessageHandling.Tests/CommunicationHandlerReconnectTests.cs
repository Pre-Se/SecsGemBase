using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Logging.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// Reconnecting after a lost connection on the active side. Since GitLab secsgem/secsgembase#5 TCPIPClientBase ends
/// Connect() when its connection is lost, and only CommunicationHandler reconnects, with a new object.
/// </summary>
public class CommunicationHandlerReconnectTests
{
    private const int Drops = 20;
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    /// <summary>
    /// Active side against a remote that keeps listening, answers every Select.req and drops all connections 20 times.
    /// Every drop must bring exactly one new connection back to Selected. Before #5 the client loop connected again by
    /// itself while CommunicationHandler was replacing it, so the remote got a second connection (with FIN at the
    /// first drop, with RST within the 20 drops).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_drop_brings_back_exactly_one_connection(bool reset)
    {
        var journal = new Journal();
        using var remote = new SelectAnsweringRemote(journal);
        var logger = new RecordingLogger<CommunicationHandler>(journal);
        var connectionLogger = new RecordingLogger<TCPIPClientBase>(journal);
        using var stack = new ActiveStack(remote.Port, journal, logger, connectionLogger);

        stack.Communication.OpenPort();
        await WaitUntilAsync(() => stack.SelectedCount == 1, () => Describe(stack, remote, journal, 0));

        for (var drop = 1; drop <= Drops; drop++)
        {
            journal.Add("test", $"drop {drop}");
            remote.DropAll(reset);
            var expected = drop + 1;
            await WaitUntilAsync(() => stack.SelectedCount >= expected && stack.Selected,
                () => Describe(stack, remote, journal, drop));
            // A second connection from the replaced loop would follow right away.
            await Task.Delay(100);
            var (accepted, selected, disconnects) = (remote.Accepted, stack.SelectedCount, stack.DisconnectCount);
            if (accepted != expected || selected != expected || disconnects != drop)
                Assert.Fail($"after drop {drop}: accepted={accepted}, selected={selected}, disconnects={disconnects}\n{journal}");
        }

        Assert.DoesNotContain(connectionLogger.Messages, m => m.Contains("did not stop within timeout"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Unhandled exception"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Restarting the connection failed"));
    }

    /// <summary>
    /// The remote drops the first connection instead of answering its Select.req. The restart's OnDisconnect must
    /// cancel the pending transaction; otherwise T6 would expire later and restart the successor, which by then is
    /// Selected (one extra connection). T6 also applies to the successor's own Select.req, hence 1 s rather than less.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Drop_while_the_Select_req_is_pending_brings_back_exactly_one_connection(bool reset)
    {
        var journal = new Journal();
        using var remote = new SelectAnsweringRemote(journal) { DropOnSelect = index => index == 1 ? reset : null };
        var logger = new RecordingLogger<CommunicationHandler>(journal);
        using var stack = new ActiveStack(remote.Port, journal, logger, new RecordingLogger<TCPIPClientBase>(journal), t6: 1000);

        stack.Communication.OpenPort();
        await WaitUntilAsync(() => stack.SelectedCount == 1 && stack.Selected, () => Describe(stack, remote, journal, 1));
        await Task.Delay(1300); // longer than T6

        var (accepted, selected, disconnects, status) = (remote.Accepted, stack.SelectedCount, stack.DisconnectCount, stack.Status);
        if (accepted != 2 || selected != 1 || disconnects != 1 || status != HSMSStatus.Selected)
            Assert.Fail($"accepted={accepted}, selected={selected}, disconnects={disconnects}, HSMSStatus={status}\n{journal}");
    }

    /// <summary>
    /// A restart request that arrives after the queued restart has disposed the old connection concerns the new one and
    /// must not be dropped as already covered. Here the new connection reports its loss while the restart that created
    /// it is still running (in reality: it connected and was dropped while that restart's thread was preempted after
    /// releasing the lock). Until the debounce flag was cleared right after the dispose, that OnDisconnected was
    /// dropped, and with #5 nothing else reconnects any more.
    /// </summary>
    [Fact]
    public async Task Loss_of_the_new_connection_during_its_own_restart_restarts_again()
    {
        // Index 0 is built by the constructor, index 1 by the first restart.
        var factory = new FakeConnectionFactory(index => new FakeConnection(lostOnSubscribe: index == 1));
        var parameters = new HSMSParameters();
        using var communication = new CommunicationHandler(new NetworkSettings(), factory,
            NullLogger<CommunicationHandler>.Instance, parameters, new ControlMessageFactory(parameters), EventBuses);

        communication.OpenPort();
        factory.Created[0].RaiseOnDisconnected();

        await WaitUntilAsync(() => factory.Created.Count >= 3, () => $"created={factory.Created.Count}");
        await Task.Delay(200);
        Assert.Equal(3, factory.Created.Count);
        Assert.True(communication.ConnectionOn);
    }

    /// <summary>
    /// The other side of the debounce: a request of the same loss that arrives before the queued restart has disposed
    /// the old connection is covered by that restart. Here a send fails on the old connection while the restart is
    /// still disposing it; it must not lead to a second restart (a second connection at the remote side).
    /// </summary>
    [Fact]
    public async Task Failed_send_of_the_same_loss_during_the_dispose_is_coalesced()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeConnectionFactory(index =>
            new FakeConnection(lostOnSubscribe: false, disposeGate: index == 0 ? gate.Task : null));
        var parameters = new HSMSParameters();
        using var communication = new CommunicationHandler(new NetworkSettings(), factory,
            NullLogger<CommunicationHandler>.Instance, parameters, new ControlMessageFactory(parameters), EventBuses);

        communication.OpenPort();
        factory.Created[0].RaiseOnDisconnected();
        await WaitUntilAsync(() => factory.Created[0].DisposeStarted, () => "dispose of connection 0 not started");

        var sent = await communication.SendStateMessage(new ControlMessageFactory(parameters).CreateSelectRequest());
        Assert.Equal(MessageStatus.Failure, sent.Status);
        gate.SetResult();

        await WaitUntilAsync(() => factory.Created.Count >= 2, () => $"created={factory.Created.Count}");
        await Task.Delay(200);
        Assert.Equal(2, factory.Created.Count);
    }

    private static string Describe(ActiveStack stack, SelectAnsweringRemote remote, Journal journal, int drop) =>
        $"drop={drop}, accepted={remote.Accepted}, selected={stack.SelectedCount}, disconnects={stack.DisconnectCount}, " +
        $"ConnectionStatus={stack.Communication.ConnectionStatus}, HSMSStatus={stack.Status}\n{journal}";

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Condition was not met within 5 s ({state()})");
            await Task.Delay(5);
        }
    }

    /// <summary>Timestamped record of the remote's, the test's and the stack's events, printed when an assertion fails.</summary>
    private sealed class Journal
    {
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly List<string> entries = [];

        public void Add(string source, string text)
        {
            var line = $"{clock.Elapsed.TotalMilliseconds,8:F1} ms [{Environment.CurrentManagedThreadId,3}] {source}: {text}";
            lock (entries) entries.Add(line);
        }

        public override string ToString()
        {
            const int Shown = 200;
            lock (entries)
            {
                if (entries.Count <= Shown) return string.Join("\n", entries);
                return $"({entries.Count - Shown} earlier lines omitted)\n" + string.Join("\n", entries.Skip(entries.Count - Shown));
            }
        }
    }

    /// <summary>
    /// Equipment side: CommunicationHandler + TransactionHandler + ControlMessageHandling with a real TCPIPClientBase per
    /// connection, T5 = 1 s and Select.req on connect.
    /// </summary>
    private sealed class ActiveStack : IDisposable
    {
        public CommunicationHandler Communication { get; }
        private ControlMessageHandling Control { get; }
        public HSMSStatus Status => Control.HSMSStatus;
        public bool Selected => Status == HSMSStatus.Selected;
        public int SelectedCount => Volatile.Read(ref selectedCount);
        public int DisconnectCount => Volatile.Read(ref disconnectCount);
        private int selectedCount;
        private int disconnectCount;

        public ActiveStack(int port, Journal journal, ILogger<CommunicationHandler> logger,
            ILogger<TCPIPClientBase> connectionLogger, uint t6 = 5000)
        {
            var settings = new NetworkSettings
            {
                IpAddress = IPAddress.Loopback.ToString(),
                Port = (ushort)port,
                ConnectionMode = ConnectionMode.Active
            };
            var parameters = new HSMSParameters { T5 = 1000, T6 = t6, InitiateSelectRequest = true };
            var controlMessages = new ControlMessageFactory(parameters);

            Communication = new CommunicationHandler(settings, new ClientFactory(settings, connectionLogger, journal),
                logger, parameters, controlMessages, EventBuses);
            Communication.OnDisconnect += (_, _) => Interlocked.Increment(ref disconnectCount);
            var transactions = new TransactionHandler(parameters, new RecordingLogger<TransactionHandler>(journal),
                Communication, EventBuses);
            Control = new ControlMessageHandling(Communication, new RecordingLogger<ControlMessageHandling>(journal),
                parameters, controlMessages, transactions);
            Communication.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CommunicationHandler.ConnectionStatus))
                    journal.Add("status", $"ConnectionStatus={Communication.ConnectionStatus}");
            };
            Control.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ControlMessageHandling.HSMSStatus)) return;
                journal.Add("status", $"HSMSStatus={Status}");
                if (Selected) Interlocked.Increment(ref selectedCount);
            };
        }

        public void Dispose()
        {
            Communication.ClosePort();
            Communication.Dispose();
        }
    }

    private sealed class ClientFactory(INetworkSettings settings, ILogger<TCPIPClientBase> logger, Journal journal)
        : INetworkConnectionFactory
    {
        private int created;

        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode)
        {
            var index = Interlocked.Increment(ref created);
            var client = new TCPIPClientBase(settings, logger, EventBuses);
            journal.Add("factory", $"client {index} created");
            client.OnConnect += (_, _) => journal.Add("factory", $"client {index} OnConnect");
            client.OnDisconnected += (_, _) => journal.Add("factory", $"client {index} OnDisconnected");
            return client;
        }
    }

    /// <summary>
    /// Host stand-in: keeps listening on a port of its own (no free-port guess), answers Select.req (and Linktest.req),
    /// counts and keeps every accepted connection.
    /// </summary>
    private sealed class SelectAnsweringRemote : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly List<Socket> open = [];
        private readonly Journal journal;
        private int accepted;

        public int Port { get; }
        public int Accepted => Volatile.Read(ref accepted);

        /// <summary>For the connection with this 1-based index: drop it (true: RST, false: FIN) instead of answering
        /// its Select.req; null: answer.</summary>
        public Func<int, bool?> DropOnSelect { get; init; } = _ => null;

        public SelectAnsweringRemote(Journal journal)
        {
            this.journal = journal;
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
                    lock (open)
                    {
                        if (stop.IsCancellationRequested)
                        {
                            socket.Close(0);
                            return;
                        }
                        open.Add(socket);
                    }
                    var index = Interlocked.Increment(ref accepted);
                    journal.Add("remote", $"accepted #{index} from {socket.RemoteEndPoint}");
                    _ = Task.Run(() => AnswerControlMessages(socket, index));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private async Task AnswerControlMessages(Socket socket, int index)
        {
            var length = new byte[4];
            try
            {
                while (true)
                {
                    if (!await ReadExactAsync(socket, length)) return;
                    var message = new byte[BinaryPrimitives.ReadUInt32BigEndian(length)];
                    if (!await ReadExactAsync(socket, message)) return;
                    if (message.Length < 10) continue;
                    // Header: session id (2), byte 2, byte 3, PType, SType, system bytes (4).
                    journal.Add("remote", $"#{index} received SType {message[5]}");
                    if (message[5] == (byte)SessionType.SelectReq && DropOnSelect(index) is { } reset)
                    {
                        // Whoever removes the socket from 'open' closes it: DropAll may have taken it already.
                        bool mine;
                        lock (open) mine = open.Remove(socket);
                        if (mine) Drop(socket, reset);
                        return;
                    }
                    var reply = message[5] switch
                    {
                        (byte)SessionType.SelectReq => (byte)SessionType.SelectRsp,
                        (byte)SessionType.LinktestReq => (byte)SessionType.LinktestRsp,
                        _ => (byte)0
                    };
                    if (reply == 0) continue;
                    byte[] frame = [0, 0, 0, 10, message[0], message[1], 0, 0, 0, reply, message[6], message[7], message[8], message[9]];
                    await socket.SendAsync(frame);
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        }

        private static async Task<bool> ReadExactAsync(Socket socket, byte[] buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await socket.ReceiveAsync(buffer.AsMemory(read));
                if (n == 0) return false;
                read += n;
            }
            return true;
        }

        /// <summary>
        /// Closes the connections accepted so far. Only those: the active side reconnects within a millisecond, and a
        /// connection accepted while this runs is that reconnect, not one to drop.
        /// </summary>
        public void DropAll(bool reset)
        {
            Socket[] sockets;
            lock (open)
            {
                sockets = [.. open];
                open.Clear();
            }

            foreach (var socket in sockets) Drop(socket, reset);
        }

        /// <summary>RST: Close(0) without Shutdown; FIN: Shutdown, then Close.</summary>
        private void Drop(Socket socket, bool reset)
        {
            journal.Add("remote", $"dropping {(reset ? "RST" : "FIN")} {socket.RemoteEndPoint}");
            if (reset)
            {
                socket.Close(0);
            }
            else
            {
                try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
                socket.Close();
            }
        }

        public void Dispose()
        {
            lock (open) stop.Cancel();
            listener.Stop();
            DropAll(reset: true);
        }
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
    /// Never connects. With <paramref name="lostOnSubscribe"/> it reports its loss to an OnDisconnected handler as soon
    /// as the handler is subscribed, i.e. inside the restart that builds it. With <paramref name="disposeGate"/>,
    /// Dispose() waits for it, so the restart holds the connection lock until the test opens the gate.
    /// </summary>
    private sealed class FakeConnection(bool lostOnSubscribe, Task? disposeGate = null) : ITCPIPBase
    {
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EventHandler? onDisconnected;

        public event EventHandler? OnConnect { add { } remove { } }

        public event EventHandler? OnDisconnected
        {
            add
            {
                onDisconnected += value;
                if (lostOnSubscribe) value?.Invoke(this, EventArgs.Empty);
            }
            remove => onDisconnected -= value;
        }

        public void RaiseOnDisconnected() => onDisconnected?.Invoke(this, EventArgs.Empty);

        public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; } = EventBuses.CreateTransient<ReadOnlyMemory<byte>>();
        public Task Connect() => stopped.Task;
        public Task<bool> SendData(ReadOnlyMemory<byte> data) => Task.FromResult(false);
        public bool DisposeStarted => Volatile.Read(ref disposeStarted);
        private bool disposeStarted;

        public void Dispose()
        {
            Volatile.Write(ref disposeStarted, true);
            disposeGate?.Wait();
            stopped.TrySetResult();
        }
    }

    private sealed class RecordingLogger<T>(Journal journal) : ILogger<T>
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
            var message = formatter(state, exception);
            lock (messages) messages.Add(message);
            journal.Add(typeof(T).Name, $"{logLevel}: {message}");
        }
    }
}
