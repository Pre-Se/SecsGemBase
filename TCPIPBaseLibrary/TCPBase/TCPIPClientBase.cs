using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using SecsGemHelperClasses.Interfaces;
using TCPIPBaseLibrary.Interfaces;

namespace TCPIPBaseLibrary.TCPBase;

/// <summary>
/// Connects to a server, sends and receives data to the connected server
/// </summary>
public class TCPIPClientBase(INetworkSettings networkSettings, ILogger<TCPIPClientBase> logger, IEventBusFactory eventBusFactory) : ITCPIPBase
{
    /// <summary>
    /// <c>true</c> if this instance of <see cref="TCPIPClientBase"/> has been disposed, otherwise <c>false</c>
    /// </summary>
    private int isDisposed;

    /// <summary>
    /// Time it takes for a new connection attempt to be made after a failed one in milliseconds
    /// </summary>
    public int ConnectSeparationTimeout = 10000;

    public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; } = eventBusFactory.CreateTransient<ReadOnlyMemory<byte>>();
    private TcpClient? activeTcpClient;

    /// <summary>1 once <see cref="Abort"/> was called: the loop then closes its connection with a reset.</summary>
    private int aborting;
    private string IpAddress { get; } = networkSettings.IpAddress;
    private int Port { get; } = networkSettings.Port;
    private CancellationTokenSource ConnectionCancellationTokenSource { get; } = new();
    private ILogger<TCPIPClientBase> Logger { get; } = logger;
    private readonly SemaphoreSlim connectionSemaphore = new(1, 1);
    private Task? receiveDataLoopTask;

    public event EventHandler? OnConnect;
    public event EventHandler? OnDisconnected;

    /// <summary>
    /// Connects to the server and receives data until the connection ends
    /// </summary>
    /// <remarks>
    /// Failed connection attempts are retried every <see cref="ConnectSeparationTimeout"/> milliseconds until
    /// <see cref="Dispose()"/>. A connection that has been established and is then lost is not re-established here:
    /// the returned task completes and <see cref="OnDisconnected"/> is raised once, as it is after
    /// <see cref="Dispose()"/>. The owner decides whether to connect again, by calling <see cref="Connect"/> again or,
    /// like CommunicationHandler, with a new object. <see cref="OnDisconnected"/> is raised before the task completes,
    /// also when it faults because an <see cref="OnConnect"/> handler threw. A call while another one is running
    /// returns at once with a warning and raises nothing; after <see cref="Dispose()"/> it throws
    /// <see cref="ObjectDisposedException"/>.
    /// </remarks>
    /// <returns>A task that completes when the connection has ended or this object has been disposed</returns>
    public async Task Connect()
    {
        if (!await connectionSemaphore.WaitAsync(0))
        {
            Logger.LogWarning("Connection loop is already running.");
            return;
        }
        try
        {
            receiveDataLoopTask = ConnectAndReceiveDataLoop();
            await receiveDataLoopTask;
        }
        finally
        {
            try { connectionSemaphore.Release(); } catch (ObjectDisposedException) { }
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ConnectAndReceiveDataLoop()
    {
        var cancellationToken = ConnectionCancellationTokenSource.Token;

        while (!cancellationToken.IsCancellationRequested && isDisposed == 0)
        {
            using var tcpClient = new TcpClient();
            var wasConnected = false;
            try
            {
                await tcpClient.ConnectAsync(IpAddress, Port, cancellationToken);
                // Take the stream before SendData can reach the client and before OnConnect: a handler whose send
                // fails (the remote side has already reset) leaves the socket "not connected", and GetStream() would
                // then throw InvalidOperationException out of the loop.
                var stream = tcpClient.GetStream();
                Volatile.Write(ref activeTcpClient, tcpClient);
                wasConnected = true;
                OnConnect?.Invoke(this, EventArgs.Empty);
                await this.ReceiveDataLoopAsync(stream, Logger, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Logger.LogInformation("Connection was stopped");
                break;
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                Logger.LogWarning("{error}", e.Message);
                if (!wasConnected)
                {
                    Logger.LogInformation("Waiting {timeout} milliseconds to try again", ConnectSeparationTimeout);
                    try { await Task.Delay(ConnectSeparationTimeout, cancellationToken); }
                    catch (OperationCanceledException)
                    {
                        Logger.LogInformation("Connection was stopped");
                        break;
                    }
                }
            }
            finally
            {
                Volatile.Write(ref activeTcpClient, null);
                // Here, before the using disposes the client (which would send FIN), also when Abort() came in while
                // the connect was still completing.
                if (Volatile.Read(ref aborting) == 1) ResetConnection(tcpClient);
            }

            // Only failed connect attempts are retried here. A lost connection ends the loop: Connect() then raises
            // OnDisconnected once, and the owner decides whether to connect again. CommunicationHandler restarts with
            // a new object; reconnecting here as well raced that restart and could put a second connection on the
            // remote side (GitLab secsgem/secsgembase#5).
            if (wasConnected) break;
        }
    }

    /// <summary>
    /// Sends data to the server
    /// </summary>
    /// <param name="data"></param>
    /// <remarks>
    /// Once <see cref="Dispose()"/> has started it throws <see cref="OperationCanceledException"/>, also for a write that
    /// Dispose() interrupts.
    /// </remarks>
    public async Task<bool> SendData(ReadOnlyMemory<byte> data)
    {
        ThrowIfDisposing();
        try
        {
            var client = Volatile.Read(ref activeTcpClient);
            if (client is null or { Connected: false })
            {
                ThrowIfDisposing();
                return false;
            }

            var networkStream = client.GetStream();
            await networkStream.WriteAsync(data, ConnectionCancellationTokenSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException && Volatile.Read(ref isDisposed) == 1)
        {
            // Dispose() ended the connection or disposed the token source while this write ran.
            throw new OperationCanceledException("The connection is being disposed", e);
        }
    }

    private void ThrowIfDisposing()
    {
        if (Volatile.Read(ref isDisposed) == 1) throw new OperationCanceledException("The connection is being disposed");
    }

    /// <summary>
    /// Ends the connection with a TCP reset (RST) instead of the orderly close of <see cref="Dispose()"/>, then disposes
    /// this object like Dispose(). Without an established connection it only disposes.
    /// </summary>
    public void Abort()
    {
        // Not after Dispose(): that closed in order already, even if its loop is still finishing (Dispose() does not
        // wait for a loop that has not stored its task yet).
        if (Volatile.Read(ref isDisposed) == 1) return;
        Volatile.Write(ref aborting, 1);
        Dispose();
    }

    /// <summary>
    /// Disconnects from the server
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        // In case _isDisposed is 0, atomically set it to 1.
        // Enter the branch only if the original value is 0.
        if (Interlocked.CompareExchange(ref isDisposed, 1, 0) != 0) return;
        if (!disposing) return;

        ConnectionCancellationTokenSource.Cancel();

        if (receiveDataLoopTask != null)
        {
            try
            {
                if (!receiveDataLoopTask.Wait(TimeSpan.FromSeconds(5)))
                    Logger.LogWarning("Connection loop did not stop within timeout, continuing disposal");
            }
            catch (OperationCanceledException) { }
            catch (AggregateException ae) when (ae.InnerExceptions.All(e =>
                e is OperationCanceledException or IOException or SocketException)) { }
            catch (AggregateException ae)
            {
                // Dispose must not throw: callers (CommunicationHandler.RestartConnection) would stop halfway.
                Logger.LogWarning("Connection loop ended with {Error}", ae.InnerException?.Message);
            }
        }

        // Also here, not only in the loop: Dispose() may have stopped waiting for the loop (5 s) or not waited at all
        // (task not stored yet), and Abort() should have reset the connection when it returns, as on the server.
        if (Volatile.Read(ref aborting) == 1 && Volatile.Read(ref activeTcpClient) is { } client) ResetConnection(client);

        ConnectionCancellationTokenSource.Dispose();
        connectionSemaphore.Dispose();
    }

    private void ResetConnection(TcpClient client)
    {
        if (TCPIPExtensions.Reset(client)) Logger.LogInformation("Connection reset (TCP RST)");
    }
}