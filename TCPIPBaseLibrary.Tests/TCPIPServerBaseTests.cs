using System.Net;
using System.Net.Sockets;
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
