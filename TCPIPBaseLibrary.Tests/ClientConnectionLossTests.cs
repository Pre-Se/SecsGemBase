using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemHelperClasses;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.Network;
using TCPIPBaseLibrary.TCPBase;
using Xunit;

namespace TCPIPBaseLibrary.Tests;

/// <summary>
/// TCPIPClientBase retries failed connection attempts, but a connection that is lost ends Connect(): the owner decides
/// about reconnecting. Before the fix the loop connected again by itself while CommunicationHandler was already
/// replacing the object, so the remote side got a second connection (GitLab secsgem/secsgembase#5).
/// </summary>
public class ClientConnectionLossTests
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_connection_ends_Connect_without_connecting_again(bool reset)
    {
        using var remote = new Remote();
        using var client = Client(remote.Port);
        var connects = 0;
        var disconnects = 0;
        client.OnConnect += (_, _) => Interlocked.Increment(ref connects);
        client.OnDisconnected += (_, _) => Interlocked.Increment(ref disconnects);

        var loop = Task.Run(client.Connect);
        await WaitUntilAsync(() => remote.Accepted == 1 && Volatile.Read(ref connects) == 1,
            () => $"accepted={remote.Accepted}, connects={connects}");

        remote.DropAll(reset);

        var ended = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5))) == loop;
        Assert.True(ended, $"Connect() kept running after the loss: accepted={remote.Accepted}, connects={connects}");
        await loop;
        Assert.Equal(1, Volatile.Read(ref disconnects));
        Assert.Equal(1, remote.Accepted);

        client.Dispose();
        Assert.Equal(1, Volatile.Read(ref disconnects));
    }

    /// <summary>
    /// Only established connections end the loop: attempts that fail because nobody listens yet are still retried, and
    /// they raise no OnDisconnected.
    /// </summary>
    [Fact]
    public async Task Failed_connect_attempts_are_still_retried()
    {
        // Bound but not listening: connection attempts to this port are refused until Listen().
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var log = new RecordingLogger<TCPIPClientBase>();
        using var client = Client(((IPEndPoint)socket.LocalEndPoint!).Port, log);
        var connects = 0;
        var disconnects = 0;
        client.OnConnect += (_, _) => Interlocked.Increment(ref connects);
        client.OnDisconnected += (_, _) => Interlocked.Increment(ref disconnects);

        var loop = Task.Run(client.Connect);
        // Wait for the client's own retry: Windows resends a refused SYN for about 2 s before ConnectAsync fails, so a
        // fixed wait could end while the first attempt is still running, and that attempt would then simply succeed.
        await WaitUntilAsync(() => log.Messages.Any(m => m.StartsWith("Waiting ")),
            () => "no retry: " + string.Join(" | ", log.Messages), TimeSpan.FromSeconds(15));
        Assert.False(loop.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref disconnects));

        socket.Listen();
        using var accepted = await socket.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => Volatile.Read(ref connects) == 1, () => $"connects={connects}");
        Assert.Equal(0, Volatile.Read(ref disconnects));
        Assert.False(loop.IsCompleted);

        client.Dispose();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref disconnects));
    }

    /// <summary>
    /// Users of TCPIPClientBase without CommunicationHandler now reconnect themselves: Connect() can be called again on
    /// the same object once it has returned, and each call raises OnDisconnected once.
    /// </summary>
    [Fact]
    public async Task Connect_can_be_called_again_after_a_lost_connection()
    {
        using var remote = new Remote();
        using var client = Client(remote.Port);
        var connects = 0;
        var disconnects = 0;
        client.OnConnect += (_, _) => Interlocked.Increment(ref connects);
        client.OnDisconnected += (_, _) => Interlocked.Increment(ref disconnects);

        await RunUntilLostAsync(1);
        await RunUntilLostAsync(2);

        Assert.Equal(2, remote.Accepted);

        async Task RunUntilLostAsync(int call)
        {
            var loop = Task.Run(client.Connect);
            await WaitUntilAsync(() => remote.Accepted == call && Volatile.Read(ref connects) == call,
                () => $"call={call}, accepted={remote.Accepted}, connects={connects}");
            remote.DropAll(reset: true);
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(call, Volatile.Read(ref disconnects));
        }
    }

    private static TCPIPClientBase Client(int port, ILogger<TCPIPClientBase>? logger = null) => new(new NetworkSettings
    {
        IpAddress = IPAddress.Loopback.ToString(),
        Port = (ushort)port,
        ConnectionMode = ConnectionMode.Active
    }, logger ?? NullLogger<TCPIPClientBase>.Instance, EventBuses)
    {
        ConnectSeparationTimeout = 50
    };

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> state, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Condition was not met in time ({state()})");
            await Task.Delay(5);
        }
    }

    /// <summary>Listens on a port of its own (no free-port guess), counts and keeps every accepted connection.</summary>
    private sealed class Remote : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly List<Socket> open = [];
        private int accepted;

        public int Port { get; }
        public int Accepted => Volatile.Read(ref accepted);

        public Remote()
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
                    lock (open)
                    {
                        if (stop.IsCancellationRequested)
                        {
                            socket.Close(0);
                            return;
                        }
                        open.Add(socket);
                    }
                    Interlocked.Increment(ref accepted);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        /// <summary>
        /// Closes the connections accepted so far, with RST (Close(0) without Shutdown) or with FIN. Only those: a
        /// connection accepted while this runs is a reconnect and stays open.
        /// </summary>
        public void DropAll(bool reset)
        {
            Socket[] sockets;
            lock (open)
            {
                sockets = [.. open];
                open.Clear();
            }

            foreach (var socket in sockets)
            {
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
        }

        public void Dispose()
        {
            lock (open) stop.Cancel();
            listener.Stop();
            DropAll(reset: true);
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
