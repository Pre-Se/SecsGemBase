using Microsoft.Extensions.Logging;
using SecsGemHelperClasses.Interfaces;
using System.Net;
using System.Net.Sockets;
using TCPIPBaseLibrary.Interfaces;

namespace TCPIPBaseLibrary.TCPBase;


/// <summary>
/// Listens for incoming connections, receives and sends data to the connected client
/// </summary>
public class TCPIPServerBase : ITCPIPBase
{
    /// <summary>
    /// <c>true</c> if the server is connected to a client as of the most recent operation, otherwise <c>false</c>
    /// </summary>
    public bool Connected => ConnectedClient is { Client.Connected: true };

    /// <summary>
    /// <c>true</c> if this instance of <see cref="TCPIPServerBase"/> has been disposed, otherwise <c>false</c>
    /// </summary>
    private int isDisposed;

    /// <summary>1 once <see cref="Abort"/> was called: Dispose then closes the connection with a reset.</summary>
    private int aborting;

    /// <summary>
    /// The IP address that the server is listening to, if empty it listens to any IP address
    /// </summary>
    public string IpAddress { get; }

    /// <summary>
    /// The port that the server is listening to
    /// </summary>
    public int Port { get; }
    public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; }
    private TcpListener TcpListener { get; }
    private TcpClient? ConnectedClient { get; set; }
    private CancellationTokenSource ConnectionCancellationTokenSource { get; set; } = new();
    private ILogger<TCPIPServerBase> Logger { get; }
    private readonly SemaphoreSlim connectionSemaphore = new(1, 1);
    private Task? receiveDataLoopTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="TCPIPServerBase"/> class using the provided <see cref="INetworkSettings"/> parameters
    /// </summary>
    public TCPIPServerBase(INetworkSettings networkSettings, ILogger<TCPIPServerBase> logger, IEventBusFactory eventBusFactory)
    {
        Logger = logger;
        IpAddress = networkSettings.IpAddress;
        Port = networkSettings.Port;
        TcpListener = CreateListener();

        OnDataReceived = eventBusFactory.CreateTransient<ReadOnlyMemory<byte>>();
    }

    public event EventHandler? OnConnect;
    public event EventHandler? OnDisconnected;

    /// <summary>
    /// Starts listening, accepts one client connection and receives until it ends
    /// </summary>
    /// <returns>A task that completes when the connection has ended or this object has been disposed; then
    /// <see cref="OnDisconnected"/> has been raised once</returns>
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
            // Dispose() may already have disposed the semaphore: it waits at most 5 s for the loop and races with
            // this finally. TCPIPClientBase.Connect handles this the same way.
            try { connectionSemaphore.Release(); } catch (ObjectDisposedException) { }
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ConnectAndReceiveDataLoop()
    {
        try
        {
            if (isDisposed == 1) return;

            TcpListener.Start();
            ConnectedClient = await TcpListener.AcceptTcpClientAsync(ConnectionCancellationTokenSource.Token);
            // Take the stream before OnConnect (see TCPIPClientBase): a failed send in a handler would make
            // GetStream() throw InvalidOperationException.
            var stream = ConnectedClient.GetStream();
            TcpListener.Stop();
            OnConnect?.Invoke(this, EventArgs.Empty);

            await this.ReceiveDataLoopAsync(stream, Logger, ConnectionCancellationTokenSource.Token);
        }
        catch (SocketException e)
        {
            Logger.LogWarning("{error}", e.Message);
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("Stopped listening for incoming connections");
        }
        catch (Exception e) when (Volatile.Read(ref isDisposed) == 1)
        {
            // Dispose() does not wait for the loop while receiveDataLoopTask is still unassigned (Connect() stores it
            // only after this method first yields, e.g. after a synchronous accept), so it may cancel and dispose the
            // client or listener while the loop runs. Depending on where that hits, GetStream() throws
            // ObjectDisposedException, InvalidOperationException or IOException, and a ReadAsync that has already passed
            // its own cancellation check (in Socket.ReceiveAsync) fails with an IOException wrapping
            // ObjectDisposedException. After Dispose() all of them only mean that the connection is gone.
            Logger.LogInformation("Connection was disposed ({Error})", e.GetType().Name);
        }
    }

    /// <summary>
    /// Sends data to the connected client
    /// </summary>
    /// <remarks>
    /// Once <see cref="Dispose()"/> has started it throws <see cref="OperationCanceledException"/>, also for a write that
    /// Dispose() interrupts (see <see cref="ITCPIPBase.SendData"/>).
    /// </remarks>
    public async Task<bool> SendData(ReadOnlyMemory<byte> data)
    {
        ThrowIfDisposing();
        try
        {
            var networkStream = ConnectedClient?.GetStream();
            if (networkStream == null)
            {
                ThrowIfDisposing();
                return false;
            }

            // With the token, Dispose() cancels a pending write. Without it, the write ended with an IOException once
            // Dispose() closed the client, and CommunicationHandler restarted the connection that replaces this one.
            await networkStream.WriteAsync(data, ConnectionCancellationTokenSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException && Volatile.Read(ref isDisposed) == 1)
        {
            // Dispose() closed the client or disposed the token source while this write ran.
            throw new OperationCanceledException("The connection is being disposed", e);
        }
    }

    private void ThrowIfDisposing()
    {
        if (Volatile.Read(ref isDisposed) == 1) throw new OperationCanceledException("The connection is being disposed");
    }

    private TcpListener CreateListener()
    {
        if (IpAddress is "")
        {
            return new TcpListener(IPAddress.Any, Port);
        }

        var serverIpAddress = IPAddress.Parse(IpAddress);

        return new TcpListener(serverIpAddress, Port);
    }


    /// <summary>
    /// Ends the connection with a TCP reset (RST) instead of the orderly close of <see cref="Dispose()"/>, then disposes
    /// this object like Dispose(). Without a connected client it only disposes.
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
    /// Disconnects from the client
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
            catch (AggregateException ae)
            {
                // Task.Wait wraps a faulted loop; Dispose must not throw (see TCPIPClientBase).
                Logger.LogWarning("Connection loop ended with {Error}", ae.InnerException?.Message);
            }
        }

        ConnectionCancellationTokenSource.Dispose();
        TcpListener.Stop();
        TcpListener.Dispose();
        if (ConnectedClient != null)
        {
            if (Volatile.Read(ref aborting) == 1 && TCPIPExtensions.Reset(ConnectedClient))
                Logger.LogInformation("Connection reset (TCP RST)");
            ConnectedClient.Close();
            ConnectedClient.Dispose();
        }
        connectionSemaphore.Dispose();
    }
}
