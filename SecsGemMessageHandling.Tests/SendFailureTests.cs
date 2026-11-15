using System.Net;
using System.Net.Sockets;
using Logging.Enums;
using Logging.Interfaces;
using Microsoft.Extensions.Logging;
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
using Xunit.Abstractions;

namespace SecsGemMessageHandling.Tests;

/// <summary>
/// A message that could not be written is reported as Failure, never as Success and never as an exception (GitLab
/// secsgem/secsgembase#14). Until then SendDataMessage reported an IOException as Success (published on
/// OnDataMessageOut, a transaction waited T3), let every other exception through to the caller, and SendTransaction
/// then left its entry in ReplyExpectedMessages.
/// </summary>
public class SendFailureTests(ITestOutputHelper output)
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    [Theory]
    [InlineData("false")]
    [InlineData(nameof(IOException))]
    [InlineData(nameof(SocketException))]
    [InlineData(nameof(InvalidOperationException))]
    [InlineData(nameof(ObjectDisposedException))]
    public async Task Failed_write_is_reported_as_Failure_and_restarts_the_connection(string failure)
    {
        var factory = new FakeConnectionFactory { Write = Failure(failure) };
        using var stack = new Stack(factory);
        var published = new List<uint>();
        stack.Communication.OnDataMessageOut.Subscribe(m => { lock (published) published.Add(m.HeaderData.SystemBytes); });

        var failed = await stack.Communication.SendAndLogMessage(EventReport(), 1);

        Assert.Equal(MessageStatus.Failure, failed.Status);
        await WaitUntilAsync(() => factory.Created.Count == 2, () => $"created={factory.Created.Count}");
        Assert.Contains(stack.Log.Messages, m => m.StartsWith("Error sending"));

        // A message that is written is published; the failed one before it was not (the bus keeps the order).
        factory.Write = () => true;
        var written = await stack.Communication.SendAndLogMessage(EventReport(), 2);
        Assert.Equal(MessageStatus.Success, written.Status);
        await WaitUntilAsync(() => { lock (published) return published.Count > 0; }, () => "nothing published");
        lock (published) Assert.Equal([2u], published);
    }

    /// <summary>
    /// OperationCanceledException means the connection object is being disposed (TCPIPClientBase cancels its writes in
    /// Dispose(); WaffleMaker's TCP decorator throws it for a replaced connection): whoever disposes it is already
    /// replacing or closing the connection, so the send fails without requesting another restart. Until #14 it reached
    /// the caller of SendAndLogMessage and SendStateMessage.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_cancelled_by_Dispose_is_a_Failure_without_restart(bool taskCanceled)
    {
        var factory = new FakeConnectionFactory
        {
            Write = () => throw (taskCanceled ? new TaskCanceledException() : new OperationCanceledException())
        };
        using var stack = new Stack(factory);

        var data = await stack.Communication.SendAndLogMessage(EventReport(), 1);
        var control = await stack.Communication.SendStateMessage(stack.ControlMessages.CreateSelectRequest());

        Assert.Equal(MessageStatus.Failure, data.Status);
        Assert.Equal(MessageStatus.Failure, control.Status);
        Assert.Equal(2, stack.Log.Messages.Count(m => m.EndsWith("not sent, the connection is being closed")));
        // A restart is queued on the thread pool; give a wrongly queued one time to show up.
        await Task.Delay(300);
        Assert.Single(factory.Created);

        // In the same setup a write error does restart.
        factory.Write = () => throw new IOException("Connection reset by peer");
        await stack.Communication.SendAndLogMessage(EventReport(), 2);
        await WaitUntilAsync(() => factory.Created.Count == 2, () => $"created={factory.Created.Count}");
    }

    /// <summary>
    /// SendTransaction ends at once with MessageNotSent and removes its entry. Until #14 an IOException let it wait for
    /// T3 (here 30 s), and an ObjectDisposedException or OperationCanceledException was thrown and left the entry.
    /// </summary>
    [Theory]
    [InlineData("false")]
    [InlineData(nameof(IOException))]
    [InlineData(nameof(ObjectDisposedException))]
    [InlineData(nameof(OperationCanceledException))]
    public async Task Transaction_with_a_failed_write_ends_at_once_with_MessageNotSent(string failure)
    {
        var factory = new FakeConnectionFactory { Write = Failure(failure) };
        using var stack = new Stack(factory);

        var (error, reply) = await stack.Transactions.SendTransaction(AreYouThere()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TransactionHandlerError.MessageNotSent, error);
        Assert.Null(reply);
        Assert.Empty(stack.Transactions.ReplyExpectedMessages);
    }

    /// <summary>
    /// An exception that is not a write error still reaches the caller, but no longer leaves the entry. It is not taken
    /// for the wait's T3 timeout either (a TimeoutException from sending, e.g. from a decorator that limits writes).
    /// </summary>
    [Theory]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(TimeoutException))]
    public async Task Transaction_whose_send_throws_passes_the_exception_on_and_removes_its_entry(Type exception)
    {
        var factory = new FakeConnectionFactory { Write = () => throw (Exception)Activator.CreateInstance(exception)! };
        using var stack = new Stack(factory);

        var thrown = await Record.ExceptionAsync(() => stack.Transactions.SendTransaction(AreYouThere()));

        Assert.IsType(exception, thrown);
        Assert.Empty(stack.Transactions.ReplyExpectedMessages);
        Assert.DoesNotContain(stack.TransactionLog.Messages, m => m.StartsWith("T3 Timeout"));
        Assert.Single(factory.Created);
    }

    /// <summary>
    /// A passive handler that had a peer is disposed without ClosePort() (still ConnectionOn, GitLab
    /// secsgem/secsgembase#62), then sends, e.g. from a timer. The disposed TCPIPServerBase reports that as being
    /// disposed, so the send fails without a restart. Otherwise the restart would listen on the port again: an
    /// ObjectDisposedException from GetStream() of the disposed client counts as a write error.
    /// </summary>
    [Fact]
    public async Task Send_after_Dispose_of_a_passive_handler_does_not_open_the_port_again()
    {
        var port = GetFreePort();
        var settings = new NetworkSettings
        {
            IpAddress = IPAddress.Loopback.ToString(),
            Port = (ushort)port,
            ConnectionMode = ConnectionMode.Passive
        };
        var parameters = new HSMSParameters();
        var factory = new ServerFactory(settings);
        var communication = new CommunicationHandler(settings, factory, NullLogger<CommunicationHandler>.Instance,
            parameters, new ControlMessageFactory(parameters), EventBuses);
        communication.OpenPort();
        using var peer = new TcpClient();
        await ConnectWithRetryAsync(peer, port);
        await WaitUntilAsync(() => communication.ConnectionStatus == ConnectionStatus.Connected,
            () => $"{communication.ConnectionStatus}");

        communication.Dispose();
        var sent = await communication.SendAndLogMessage(EventReport(), 1);

        Assert.Equal(MessageStatus.Failure, sent.Status);
        // A restart is queued on the thread pool; give a wrongly queued one time to show up.
        await Task.Delay(300);
        Assert.Equal(1, factory.Created);
        using var probe = new TcpClient();
        var refused = await Record.ExceptionAsync(() => probe.ConnectAsync(IPAddress.Loopback, port));
        Assert.IsType<SocketException>(refused);
    }

    /// <summary>
    /// Real sockets: the remote side resets every connection as soon as it has read the first bytes, and the client
    /// reconnects, while the test keeps sending. Depending on the timing a send meets an IOException, an
    /// ObjectDisposedException, a connection that is being disposed or no connection at all. Whatever it meets, it must
    /// not throw and must not report Success after logging an error; until #14 the IOException gave exactly that (the
    /// ticket saw it once after every reset on the passive side), and the ObjectDisposedException was thrown.
    /// </summary>
    [Fact]
    public async Task Sends_racing_a_reset_never_throw_and_never_report_Success_after_an_error()
    {
        using var remote = new ResettingRemote();
        var log = new RecordingLogger<CommunicationHandler>();
        var settings = new NetworkSettings
        {
            IpAddress = IPAddress.Loopback.ToString(),
            Port = (ushort)remote.Port,
            ConnectionMode = ConnectionMode.Active
        };
        var parameters = new HSMSParameters { T5 = 50 };
        using var communication = new CommunicationHandler(settings, new ClientFactory(settings), log, parameters,
            new ControlMessageFactory(parameters), EventBuses);
        communication.OpenPort();
        try
        {
            await SendUntilFailuresAsync(communication, log, remote);
        }
        finally
        {
            // Before Dispose(): a restart that is still queued would otherwise start a new connection loop after it,
            // which keeps reconnecting until the test host exits (GitLab secsgem/secsgembase#62).
            communication.ClosePort();
        }
    }

    private async Task SendUntilFailuresAsync(CommunicationHandler communication,
        RecordingLogger<CommunicationHandler> log, ResettingRemote remote)
    {
        // Here (Linux, loopback) about a quarter of the failures meet an exception and a fifth a write cancelled by
        // the restart; the others find no connection any more.
        const int failuresWanted = 100;
        var paths = new Dictionary<string, int>();
        var exceptions = new HashSet<string>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        uint systemBytes = 0;
        while (paths.Values.Sum() < failuresWanted)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Only {paths.Values.Sum()} failures in 60 s, {remote.Accepted} accepted");
            // Mostly send while connected: every send without a connection restarts it again (GitLab
            // secsgem/secsgembase#32). The status still says Connected for a moment after the reset.
            await WaitUntilAsync(() => communication.ConnectionStatus == ConnectionStatus.Connected,
                () => $"{communication.ConnectionStatus}, accepted={remote.Accepted}", TimeSpan.FromSeconds(10));

            systemBytes++;
            var logged = log.Messages.Count;
            ILoggedDataMessage sent;
            try
            {
                sent = await communication.SendAndLogMessage(EventReport(), systemBytes);
            }
            catch (Exception e)
            {
                Assert.Fail($"Send {systemBytes} threw {e.GetType().Name}: {e.Message}");
                throw;
            }

            // Only this send writes these lines; restarts log other ones.
            var lines = log.Messages.Skip(logged)
                .Where(m => m.StartsWith("Error sending") || m.EndsWith("not sent, the connection is being closed"))
                .ToList();
            Assert.False(sent.Status == MessageStatus.Success && lines.Count > 0,
                $"Send {systemBytes} reported Success after: {string.Join(" | ", lines)}");
            if (sent.Status == MessageStatus.Success) continue;

            Assert.Single(lines);
            var path = !lines[0].StartsWith("Error sending") ? "cancelled"
                : lines[0].Contains(" : ") ? "exception" : "no connection";
            paths[path] = paths.GetValueOrDefault(path) + 1;
            if (path == "exception") exceptions.Add(lines[0]);
        }

        output.WriteLine($"{systemBytes} sends, {remote.Accepted} connections; failures: " +
                         string.Join(", ", paths.Select(p => $"{p.Key}={p.Value}")));
        foreach (var e in exceptions) output.WriteLine(e);
    }

    private static Func<bool> Failure(string failure) => failure switch
    {
        "false" => () => false,
        nameof(IOException) => () => throw new IOException("Connection reset by peer"),
        nameof(SocketException) => () => throw new SocketException((int)SocketError.ConnectionReset),
        nameof(InvalidOperationException) => () => throw new InvalidOperationException("The operation is not allowed on non-connected sockets."),
        nameof(ObjectDisposedException) => () => throw new ObjectDisposedException("System.Net.Sockets.TcpClient"),
        nameof(OperationCanceledException) => () => throw new OperationCanceledException(),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null)
    };

    private static int GetFreePort()
    {
        // A port outside the ephemeral range, see TestPorts (GitLab secsgem/secsgembase#61).
        return TestSupport.TestPorts.Next();
    }

    private static async Task ConnectWithRetryAsync(TcpClient client, int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                // The server is not listening yet.
                await Task.Delay(20);
            }
        }
    }

    private static SecsGemDataMessage EventReport() => new MessageFactory(6, 11, false, "Event Report Send").Build();
    private static SecsGemDataMessage AreYouThere() => new MessageFactory(1, 1, true, "Are You There Request").Build();

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> state, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Condition was not met in time ({state()})");
            await Task.Delay(5);
        }
    }

    /// <summary>CommunicationHandler + TransactionHandler on fake connections; the port is never opened.</summary>
    private sealed class Stack : IDisposable
    {
        public HSMSParameters Parameters { get; } = new() { T3 = 30000 };
        public ControlMessageFactory ControlMessages { get; }
        public RecordingLogger<CommunicationHandler> Log { get; } = new();
        public RecordingLogger<TransactionHandler> TransactionLog { get; } = new();
        public CommunicationHandler Communication { get; }
        public TransactionHandler Transactions { get; }

        public Stack(INetworkConnectionFactory factory)
        {
            ControlMessages = new ControlMessageFactory(Parameters);
            Communication = new CommunicationHandler(new NetworkSettings { ConnectionMode = ConnectionMode.Active },
                factory, Log, Parameters, ControlMessages, EventBuses);
            Transactions = new TransactionHandler(Parameters, TransactionLog, Communication, EventBuses);
        }

        public void Dispose() => Communication.Dispose();
    }

    private sealed class FakeConnectionFactory : INetworkConnectionFactory
    {
        private readonly List<FakeConnection> created = [];

        /// <summary>What every connection's SendData does: return a value or throw.</summary>
        public volatile Func<bool> Write = () => true;

        public IReadOnlyList<FakeConnection> Created
        {
            get { lock (created) return created.ToList(); }
        }

        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode)
        {
            var connection = new FakeConnection(this);
            lock (created) created.Add(connection);
            return connection;
        }
    }

    private sealed class FakeConnection(FakeConnectionFactory factory) : ITCPIPBase
    {
        // Never raised: the port is never opened.
        public event EventHandler? OnConnect { add { } remove { } }
        public event EventHandler? OnDisconnected { add { } remove { } }
        public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; } = EventBuses.CreateTransient<ReadOnlyMemory<byte>>();
        public Task Connect() => Task.Delay(Timeout.Infinite);

        public async Task<bool> SendData(ReadOnlyMemory<byte> data)
        {
            await Task.Yield();
            return factory.Write();
        }

        private int disposed;

        // Once only: a queued restart and CommunicationHandler.Dispose() at the end of a test may both dispose this
        // object (Dispose() does not take the connection lock, GitLab secsgem/secsgembase#62), and EventBus.Dispose()
        // throws the second time.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) OnDataReceived.Dispose();
        }
    }

    private sealed class ServerFactory(INetworkSettings settings) : INetworkConnectionFactory
    {
        private int created;
        public int Created => Volatile.Read(ref created);

        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode)
        {
            Interlocked.Increment(ref created);
            return new TCPIPServerBase(settings, NullLogger<TCPIPServerBase>.Instance, EventBuses);
        }
    }

    private sealed class ClientFactory(INetworkSettings settings) : INetworkConnectionFactory
    {
        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode) =>
            new TCPIPClientBase(settings, NullLogger<TCPIPClientBase>.Instance, EventBuses);
    }

    /// <summary>Accepts every connection and resets it (RST) as soon as it has read the first bytes.</summary>
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
                    Socket socket;
                    try
                    {
                        socket = await listener.AcceptSocketAsync(stop.Token);
                    }
                    catch (SocketException)
                    {
                        continue; // e.g. a connection that was reset before it was accepted
                    }
                    Interlocked.Increment(ref accepted);
                    _ = Task.Run(async () =>
                    {
                        try { await socket.ReceiveAsync(new byte[64], SocketFlags.None, stop.Token); }
                        catch (Exception e) when (e is OperationCanceledException or SocketException) { }
                        finally { socket.Close(0); }
                    });
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
