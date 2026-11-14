using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemHelperClasses;
using SecsGemMessageHandling.Data_Handling;
using SecsGemMessageHandling.Enums;
using SecsGemMessageHandling.Helpers;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.Network;
using TCPIPBaseLibrary.TCPBase;
using Xunit;

namespace SecsGemMessageHandling.Tests;

/// <summary>
/// ClosePort(abort: true) and RestartConnection(abort: true) end the connection with a TCP reset (RST) instead of
/// closing it in order, to simulate a hard connection loss (GitLab secsgem/secsgembase#53). The overloads without the
/// flag keep closing in order.
/// </summary>
public class AbortConnectionTests
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    [Theory]
    [InlineData(ConnectionMode.Active, true, "reset")]
    [InlineData(ConnectionMode.Active, false, "closed")]
    [InlineData(ConnectionMode.Passive, true, "reset")]
    [InlineData(ConnectionMode.Passive, false, "closed")]
    public async Task ClosePort_resets_or_closes_the_connection(ConnectionMode mode, bool abort, string expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = mode == ConnectionMode.Active ? ((IPEndPoint)listener.LocalEndpoint).Port : GetFreePort();
        if (mode == ConnectionMode.Passive) listener.Stop();
        var settings = Settings(mode, port);
        var parameters = new HSMSParameters { T5 = 100 };
        using var communication = new CommunicationHandler(settings, new ConnectionFactory(settings, new Log()),
            NullLogger<CommunicationHandler>.Instance, parameters, new ControlMessageFactory(parameters), EventBuses);
        communication.OpenPort();

        using var peer = mode == ConnectionMode.Active
            ? await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10))
            : await ConnectWithRetryAsync(port);
        await WaitUntilAsync(() => communication.ConnectionStatus == ConnectionStatus.Connected,
            () => $"{communication.ConnectionStatus}");

        if (abort) communication.ClosePort(abort: true);
        else communication.ClosePort();

        Assert.Equal(expected, await ReadEndAsync(peer));
        Assert.False(communication.ConnectionOn);
        Assert.Equal(ConnectionStatus.PortClosed, communication.ConnectionStatus);
    }

    /// <summary>
    /// After a restart with reset on either side, both sides are Selected again, each with exactly one new connection
    /// (the client does not reconnect twice, GitLab secsgem/secsgembase#5), and the other side saw a reset, not 0 bytes.
    /// </summary>
    [Theory]
    [InlineData("host", true)]
    [InlineData("equipment", true)]
    [InlineData("host", false)]
    [InlineData("equipment", false)]
    public async Task Restart_with_or_without_reset_comes_back_selected_with_one_new_connection(string side, bool abort)
    {
        var port = GetFreePort();
        using var host = new HSMSStack(ConnectionMode.Passive, port, initiateSelect: false);
        using var equipment = new HSMSStack(ConnectionMode.Active, port, initiateSelect: true);
        host.Communication.OpenPort();
        equipment.Communication.OpenPort();
        await WaitUntilAsync(() => host.Selected && equipment.Selected, () => Describe(host, equipment));
        Assert.Equal(1, host.Connects);
        Assert.Equal(1, equipment.Connects);
        var (restarting, other) = side == "host" ? (host, equipment) : (equipment, host);

        if (abort) restarting.Communication.RestartConnection(abort: true);
        else restarting.Communication.RestartConnection();

        await WaitUntilAsync(() => host.Connects == 2 && equipment.Connects == 2 && host.Selected && equipment.Selected,
            () => Describe(host, equipment));
        // A second reconnect would show up within this time.
        await Task.Delay(500);
        Assert.Equal(2, host.Connects);
        Assert.Equal(2, equipment.Connects);
        // ReceiveDataLoopAsync logs a reset with the socket error, a close (0 bytes) without.
        var ends = other.TcpLog.Messages.Where(m => m.StartsWith("Connection was reset by remote machine")).ToList();
        Assert.Single(ends);
        Assert.Equal(abort, ends[0].StartsWith("Connection was reset by remote machine: "));
    }

    private static string Describe(HSMSStack host, HSMSStack equipment) =>
        $"host: {host.Communication.ConnectionStatus}/{host.Status}, connects={host.Connects}; " +
        $"equipment: {equipment.Communication.ConnectionStatus}/{equipment.Status}, connects={equipment.Connects}";

    /// <summary>How the peer's next read ends: "closed" (FIN, 0 bytes) or "reset" (RST).</summary>
    private static async Task<string> ReadEndAsync(Socket peer)
    {
        try
        {
            var read = await peer.ReceiveAsync(new byte[64], SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10));
            return read == 0 ? "closed" : $"{read} bytes";
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
        {
            return "reset";
        }
    }

    private static INetworkSettings Settings(ConnectionMode mode, int port) => new NetworkSettings
    {
        IpAddress = IPAddress.Loopback.ToString(),
        Port = (ushort)port,
        ConnectionMode = mode
    };

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<Socket> ConnectWithRetryAsync(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
                return socket;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                // The server is not listening yet.
                await Task.Delay(20);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Condition was not met in time ({state()})");
            await Task.Delay(10);
        }
    }

    /// <summary>CommunicationHandler + TransactionHandler + ControlMessageHandling with real TCP objects.</summary>
    private sealed class HSMSStack : IDisposable
    {
        public CommunicationHandler Communication { get; }
        private ControlMessageHandling Control { get; }
        public Log TcpLog { get; } = new();
        public HSMSStatus Status => Control.HSMSStatus;
        public bool Selected => Status == HSMSStatus.Selected;
        public int Connects => Volatile.Read(ref connects);
        private int connects;

        public HSMSStack(ConnectionMode mode, int port, bool initiateSelect)
        {
            var settings = Settings(mode, port);
            var parameters = new HSMSParameters { T5 = 200, InitiateSelectRequest = initiateSelect };
            var controlMessages = new ControlMessageFactory(parameters);
            Communication = new CommunicationHandler(settings, new ConnectionFactory(settings, TcpLog),
                NullLogger<CommunicationHandler>.Instance, parameters, controlMessages, EventBuses);
            Communication.OnConnect += (_, _) => Interlocked.Increment(ref connects);
            var transactions = new TransactionHandler(parameters, NullLogger<TransactionHandler>.Instance,
                Communication, EventBuses);
            Control = new ControlMessageHandling(Communication, NullLogger<ControlMessageHandling>.Instance,
                parameters, controlMessages, transactions);
        }

        public void Dispose()
        {
            Communication.ClosePort();
            Communication.Dispose();
        }
    }

    private sealed class ConnectionFactory(INetworkSettings settings, Log log) : INetworkConnectionFactory
    {
        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode) => connectionMode switch
        {
            ConnectionMode.Active => new TCPIPClientBase(settings, log.For<TCPIPClientBase>(), EventBuses),
            ConnectionMode.Passive => new TCPIPServerBase(settings, log.For<TCPIPServerBase>(), EventBuses),
            _ => throw new ArgumentOutOfRangeException(nameof(connectionMode), connectionMode, null)
        };
    }

    /// <summary>Collects the messages of all TCP objects of one side.</summary>
    private sealed class Log
    {
        private readonly List<string> messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (messages) return messages.ToList(); }
        }

        public ILogger<T> For<T>() => new Logger<T>(this);

        private sealed class Logger<T>(Log log) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (log.messages) log.messages.Add(formatter(state, exception));
            }
        }
    }
}
