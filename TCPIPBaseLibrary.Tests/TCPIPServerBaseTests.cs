using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemHelperClasses;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.Network;
using TCPIPBaseLibrary.TCPBase;
using Xunit;

namespace TCPIPBaseLibrary.Tests;

public class TCPIPServerBaseTests
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    /// <summary>
    /// Dispose() waits at most 5 s for the receive loop and then disposes the connection semaphore, while Connect()
    /// releases the same semaphore in its finally on another thread. Connect() must not throw
    /// ObjectDisposedException when Dispose() wins that race (GitLab secsgem/secsgembase#3).
    /// Isolated, the race only hits about one run in a hundred, hence the 200 iterations.
    /// </summary>
    [Fact]
    public async Task Connect_ends_without_exception_when_disposed_while_connected()
    {
        for (var i = 0; i < 200; i++)
        {
            var port = GetFreePort();
            var server = new TCPIPServerBase(PassiveSettings(port), NullLogger<TCPIPServerBase>.Instance, EventBuses);
            var loop = Task.Run(server.Connect);
            using var client = new TcpClient();
            await ConnectWithRetryAsync(client, port);
            await WaitUntilAsync(() => server.Connected);

            server.Dispose();

            await loop.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Deterministic stand-in for a rare failure of the test above: Dispose() did not wait for the loop
    /// (receiveDataLoopTask not yet assigned) and closed the client while the first ReadAsync had already passed its
    /// cancellation check. The read then hits a closed socket handle, and NetworkStream wraps the
    /// ObjectDisposedException in an IOException. That interleaving cannot be forced from outside, so the OnConnect
    /// handler, which runs on the loop thread right before the first read, builds the same state: the disposed flag
    /// is set without cancelling (the protected Dispose(false) does nothing else), and only the accepted socket's
    /// handle is closed, not its NetworkStream or Socket.
    /// </summary>
    [Fact]
    public async Task Connect_ends_without_exception_when_the_first_read_hits_a_socket_closed_by_Dispose()
    {
        var port = GetFreePort();
        var server = new FlagOnlyDisposeServer(PassiveSettings(port));
        server.OnConnect += (_, _) =>
        {
            server.MarkDisposed();
            ConnectedClient(server)!.Client.SafeHandle.Dispose();
        };

        var loop = Task.Run(server.Connect);
        using var client = new TcpClient();
        await ConnectWithRetryAsync(client, port);

        var error = await Record.ExceptionAsync(() => loop.WaitAsync(TimeSpan.FromSeconds(10)));

        ConnectedClient(server)?.Dispose();
        Assert.True(error is null, error?.ToString());
    }

    private sealed class FlagOnlyDisposeServer(INetworkSettings settings)
        : TCPIPServerBase(settings, NullLogger<TCPIPServerBase>.Instance, EventBuses)
    {
        /// <summary>Sets the disposed flag only: the base class does nothing else for disposing == false.</summary>
        public void MarkDisposed() => Dispose(false);
    }

    // The accepted client is private; this is the only access the test needs beyond public and protected members.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ConnectedClient")]
    private static extern TcpClient? ConnectedClient(TCPIPServerBase server);

    private static INetworkSettings PassiveSettings(int port) => new NetworkSettings
    {
        IpAddress = IPAddress.Loopback.ToString(),
        Port = (ushort)port,
        ConnectionMode = ConnectionMode.Passive
    };

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // Connect() starts the listener asynchronously, so the first attempts may be refused.
    private static async Task ConnectWithRetryAsync(TcpClient client, int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met within 5 s");
            await Task.Delay(5);
        }
    }
}
