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
/// A send from an OnConnect handler that fails because the remote side has already reset the connection leaves the
/// socket "not connected". Before the fix both loops called GetStream() only after OnConnect, which then threw
/// InvalidOperationException out of Connect(), and Dispose() threw it again as AggregateException
/// (GitLab secsgem/secsgembase#6). Now the loop ends like for any reset connection.
/// </summary>
public class OnConnectSendFailureTests
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);
    private static readonly byte[] Probe = [0, 0, 0, 10, 0xFF, 0xFF, 0, 0, 0, 5, 0, 0, 0, 1];

    [Fact]
    public async Task Client_loop_ends_normally_when_a_send_in_OnConnect_fails()
    {
        using var remote = new TcpListener(IPAddress.Loopback, 0);
        remote.Start();
        var port = ((IPEndPoint)remote.LocalEndpoint).Port;
        using var stopRemote = new CancellationTokenSource();
        var resetter = ResetEveryConnectionAsync(remote, stopRemote.Token);

        var client = new TCPIPClientBase(Settings(port, ConnectionMode.Active), NullLogger<TCPIPClientBase>.Instance, EventBuses)
        {
            ConnectSeparationTimeout = 100
        };
        var sendFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnConnect += (_, _) => SendAfterRemoteReset(client, sendFailed);

        var loop = Task.Run(client.Connect);
        await sendFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);

        var dispose = Record.Exception(client.Dispose);
        Assert.Null(dispose);
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        stopRemote.Cancel();
        await resetter;
    }

    [Fact]
    public async Task Server_loop_ends_normally_when_a_send_in_OnConnect_fails()
    {
        var port = GetFreePort();
        var server = new TCPIPServerBase(Settings(port, ConnectionMode.Passive), NullLogger<TCPIPServerBase>.Instance, EventBuses);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Not disposed: a handler that starts only after the test has failed must not wait on a disposed event.
        var resetSent = new ManualResetEventSlim();
        var sendFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnConnect += (_, _) =>
        {
            accepted.TrySetResult();
            resetSent.Wait(TimeSpan.FromSeconds(10));
            SendAfterRemoteReset(server, sendFailed);
        };
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnDisconnected += (_, _) => disconnected.TrySetResult();

        var loop = Task.Run(server.Connect);
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await ConnectWithRetryAsync(socket, port);
            // Reset only once the server has accepted: whether a connection that is reset while it waits in the accept
            // queue is accepted at all depends on the system (this test timed out on the GitHub runner, Ubuntu 26.04).
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Close(0) without a prior Shutdown sends RST.
            socket.Close(0);
        }
        finally
        {
            resetSent.Set();
        }

        await sendFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The loop must end like for any reset connection: OnDisconnected, and Connect() without an exception.
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        var dispose = Record.Exception(server.Dispose);
        Assert.Null(dispose);
    }

    /// <summary>
    /// Dispose() must not throw when the loop ended with an unexpected exception (here: an OnConnect handler threw).
    /// CommunicationHandler disposes the old connection inside RestartConnection/ClosePort and would stop halfway.
    /// </summary>
    [Fact]
    public async Task Client_Dispose_does_not_throw_when_an_OnConnect_handler_threw()
    {
        using var remote = new TcpListener(IPAddress.Loopback, 0);
        remote.Start();
        var port = ((IPEndPoint)remote.LocalEndpoint).Port;
        var client = new TCPIPClientBase(Settings(port, ConnectionMode.Active), NullLogger<TCPIPClientBase>.Instance, EventBuses);
        client.OnConnect += (_, _) => throw new InvalidOperationException("handler failed");

        var loop = Task.Run(client.Connect);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Null(Record.Exception(client.Dispose));
    }

    [Fact]
    public async Task Server_Dispose_does_not_throw_when_an_OnConnect_handler_threw()
    {
        var port = GetFreePort();
        var server = new TCPIPServerBase(Settings(port, ConnectionMode.Passive), NullLogger<TCPIPServerBase>.Instance, EventBuses);
        server.OnConnect += (_, _) => throw new InvalidOperationException("handler failed");

        var loop = Task.Run(server.Connect);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await ConnectWithRetryAsync(socket, port);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Null(Record.Exception(server.Dispose));
    }

    /// <summary>
    /// Waits until the remote reset has arrived, then sends synchronously like ControlMessageHandling's Select.req.
    /// The first send after the reset fails, which marks the socket as no longer connected.
    /// </summary>
    private static void SendAfterRemoteReset(ITCPIPBase connection, TaskCompletionSource sendFailed)
    {
        Thread.Sleep(100);
        try
        {
            connection.SendData(Probe).GetAwaiter().GetResult();
            connection.SendData(Probe).GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            sendFailed.TrySetResult();
        }
    }

    private static async Task ResetEveryConnectionAsync(TcpListener listener, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var socket = await listener.AcceptSocketAsync(stop);
                socket.Close(0);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static INetworkSettings Settings(int port, ConnectionMode mode) => new NetworkSettings
    {
        IpAddress = IPAddress.Loopback.ToString(),
        Port = (ushort)port,
        ConnectionMode = mode
    };

    private static int GetFreePort()
    {
        // A port outside the ephemeral range, see TestPorts (GitLab secsgem/secsgembase#61).
        return TestSupport.TestPorts.Next();
    }

    // Connect() starts the listener asynchronously, so the first attempts may be refused.
    private static async Task ConnectWithRetryAsync(Socket socket, int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }
    }
}
