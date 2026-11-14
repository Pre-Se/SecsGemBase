using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemHelperClasses;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.Network;
using TCPIPBaseLibrary.TCPBase;
using Xunit;

namespace TCPIPBaseLibrary.Tests;

/// <summary>
/// Once Dispose() has started, SendData throws OperationCanceledException, also for a write that Dispose() interrupts
/// (ITCPIPBase.SendData). CommunicationHandler then reports the message as failed without restarting the connection that
/// replaces this one (GitLab secsgem/secsgembase#14). Before, a send after Dispose() returned false on the client and
/// threw ObjectDisposedException on the server, and the server ended an interrupted write with IOException; since #14
/// CommunicationHandler restarts on all of these.
/// </summary>
public class SendDataDisposeTests
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    [Theory]
    [InlineData(ConnectionMode.Active)]
    [InlineData(ConnectionMode.Passive)]
    public async Task Send_after_Dispose_throws_OperationCanceledException(ConnectionMode mode)
    {
        var (connection, peer, loop) = await ConnectedAsync(mode);
        using var _ = peer;

        connection.Dispose();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SendData(new byte[14]));
    }

    [Theory]
    [InlineData(ConnectionMode.Active)]
    [InlineData(ConnectionMode.Passive)]
    public async Task Write_interrupted_by_Dispose_throws_OperationCanceledException(ConnectionMode mode)
    {
        var (connection, peer, loop) = await ConnectedAsync(mode);
        using var _ = peer;

        // The peer never reads, so a write far larger than the socket buffers stays pending.
        var write = connection.SendData(new byte[64 * 1024 * 1024]);
        await Task.Delay(200);
        Assert.False(write.IsCompleted, $"The write did not block: {write.Status}");

        connection.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(10)));
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Returns the connection under test, connected; its peer socket, which never reads; and its Connect() task.</summary>
    private static async Task<(ITCPIPBase connection, Socket peer, Task loop)> ConnectedAsync(ConnectionMode mode)
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (mode == ConnectionMode.Active)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TCPIPClientBase(Settings(((IPEndPoint)listener.LocalEndpoint).Port, mode),
                NullLogger<TCPIPClientBase>.Instance, EventBuses) { ConnectSeparationTimeout = 50 };
            client.OnConnect += (_, _) => connected.TrySetResult();
            var clientLoop = Task.Run(client.Connect);
            var accepted = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return (client, accepted, clientLoop);
        }

        var port = GetFreePort();
        var server = new TCPIPServerBase(Settings(port, mode), NullLogger<TCPIPServerBase>.Instance, EventBuses);
        server.OnConnect += (_, _) => connected.TrySetResult();
        var serverLoop = Task.Run(server.Connect);
        var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
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

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (server, peer, serverLoop);
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
}
