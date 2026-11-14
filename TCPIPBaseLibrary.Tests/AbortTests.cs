using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemHelperClasses;
using SecsGemHelperClasses.Interfaces;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.Network;
using TCPIPBaseLibrary.TCPBase;
using Xunit;

namespace TCPIPBaseLibrary.Tests;

/// <summary>
/// Abort() ends the connection with a TCP reset (RST), Dispose() still closes it in order (FIN). A reset is what the
/// remote side sees after a hard connection loss; it is the closest the simulator gets to pulling the cable
/// (GitLab secsgem/secsgembase#53).
/// </summary>
public class AbortTests
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    [Theory]
    [InlineData(ConnectionMode.Active)]
    [InlineData(ConnectionMode.Passive)]
    public async Task Abort_resets_the_connection(ConnectionMode mode)
    {
        var (connection, peer, loop, disconnects) = await ConnectedAsync(mode);
        using var _ = peer;

        connection.Abort();

        Assert.Equal("reset", await ReadEndAsync(peer));
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, disconnects());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SendData(new byte[14]));
    }

    [Theory]
    [InlineData(ConnectionMode.Active)]
    [InlineData(ConnectionMode.Passive)]
    public async Task Dispose_still_closes_in_order(ConnectionMode mode)
    {
        var (connection, peer, loop, disconnects) = await ConnectedAsync(mode);
        using var _ = peer;

        connection.Dispose();

        Assert.Equal("closed", await ReadEndAsync(peer));
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, disconnects());
    }

    /// <summary>
    /// Abort() while Dispose() is still running changes nothing: Dispose() closes in order. An OnConnect handler keeps
    /// the loop busy, so Dispose() waits for it (or, if the loop has not stored its task yet, returns while the loop
    /// still has to close the client); Abort() comes in during that time.
    /// </summary>
    [Theory]
    [InlineData(ConnectionMode.Active)]
    [InlineData(ConnectionMode.Passive)]
    public async Task Abort_while_Dispose_is_running_does_not_reset(ConnectionMode mode)
    {
        using var release = new ManualResetEventSlim();
        var (connection, peer, loop, disconnects) =
            await ConnectedAsync(mode, onConnect: () => release.Wait(TimeSpan.FromSeconds(10)));
        using var _ = peer;

        var disposing = Task.Run(connection.Dispose);
        // SendData throws OperationCanceledException once Dispose() has started (it writes nothing before).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await Record.ExceptionAsync(() => connection.SendData(ReadOnlyMemory<byte>.Empty))
               is not OperationCanceledException)
        {
            Assert.True(DateTime.UtcNow < deadline, "Dispose() did not start");
            await Task.Delay(5);
        }

        connection.Abort();
        release.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("closed", await ReadEndAsync(peer));
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, disconnects());
    }

    /// <summary>
    /// The client resets the connection by the time Abort() returns, also when Dispose() does not wait for a loop that
    /// is stuck in an OnConnect handler (it waits at most 5 s, and not at all before the loop has stored its task).
    /// Until then only the loop reset the socket, after the handler had returned.
    /// </summary>
    [Fact]
    public async Task Client_has_reset_when_Abort_returns_although_its_loop_is_stuck()
    {
        using var release = new ManualResetEventSlim();
        var (connection, peer, loop, _) = await ConnectedAsync(ConnectionMode.Active,
            onConnect: () => release.Wait(TimeSpan.FromSeconds(20)));
        using var _ = peer;

        await Task.Run(connection.Abort).WaitAsync(TimeSpan.FromSeconds(10));
        var end = await ReadEndAsync(peer, TimeSpan.FromSeconds(2));
        release.Set();

        Assert.Equal("reset", end);
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>An implementation without its own Abort() (a decorator, a test fake) is disposed instead.</summary>
    [Fact]
    public void Default_Abort_disposes()
    {
        var connection = new WithoutAbort();

        ((ITCPIPBase)connection).Abort();

        Assert.True(connection.Disposed);
    }

    /// <summary>How the peer's next read ends: "closed" (FIN, 0 bytes), "reset" (RST) or "nothing" in time.</summary>
    private static async Task<string> ReadEndAsync(Socket peer, TimeSpan? timeout = null)
    {
        try
        {
            var read = await peer.ReceiveAsync(new byte[64], SocketFlags.None)
                .WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
            return read == 0 ? "closed" : $"{read} bytes";
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
        {
            return "reset";
        }
        catch (TimeoutException)
        {
            return "nothing";
        }
    }

    /// <summary>
    /// Returns the connection under test, connected; its peer socket; its Connect() task; and how often it raised
    /// OnDisconnected.
    /// </summary>
    private static async Task<(ITCPIPBase connection, Socket peer, Task loop, Func<int> disconnects)> ConnectedAsync(
        ConnectionMode mode, Action? onConnect = null)
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnects = 0;
        ITCPIPBase connection;
        Socket peer;
        Task loop;
        if (mode == ConnectionMode.Active)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            connection = new TCPIPClientBase(Settings(((IPEndPoint)listener.LocalEndpoint).Port, mode),
                NullLogger<TCPIPClientBase>.Instance, EventBuses) { ConnectSeparationTimeout = 50 };
            connection.OnConnect += (_, _) => connected.TrySetResult();
            connection.OnConnect += (_, _) => onConnect?.Invoke();
            connection.OnDisconnected += (_, _) => Interlocked.Increment(ref disconnects);
            loop = Task.Run(connection.Connect);
            peer = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        else
        {
            var port = GetFreePort();
            connection = new TCPIPServerBase(Settings(port, mode), NullLogger<TCPIPServerBase>.Instance, EventBuses);
            connection.OnConnect += (_, _) => connected.TrySetResult();
            connection.OnConnect += (_, _) => onConnect?.Invoke();
            connection.OnDisconnected += (_, _) => Interlocked.Increment(ref disconnects);
            loop = Task.Run(connection.Connect);
            peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try
                {
                    await peer.ConnectAsync(IPAddress.Loopback, port);
                    break;
                }
                catch (SocketException) when (DateTime.UtcNow < deadline)
                {
                    // The server is not listening yet.
                    await Task.Delay(20);
                }
            }
        }

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (connection, peer, loop, () => Volatile.Read(ref disconnects));
    }

    private static INetworkSettings Settings(int port, ConnectionMode mode) => new NetworkSettings
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

    private sealed class WithoutAbort : ITCPIPBase
    {
        public bool Disposed { get; private set; }
        public event EventHandler? OnConnect { add { } remove { } }
        public event EventHandler? OnDisconnected { add { } remove { } }
        public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; } = EventBuses.CreateTransient<ReadOnlyMemory<byte>>();
        public Task Connect() => Task.CompletedTask;
        public Task<bool> SendData(ReadOnlyMemory<byte> data) => Task.FromResult(false);
        public void Dispose() => Disposed = true;
    }
}
