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
    private string IpAddress { get; } = networkSettings.IpAddress;
    private int Port { get; } = networkSettings.Port;
    private CancellationTokenSource ConnectionCancellationTokenSource { get; } = new();
    private ILogger<TCPIPClientBase> Logger { get; } = logger;
    private readonly SemaphoreSlim connectionSemaphore = new(1, 1);
    private Task? receiveDataLoopTask;

    public event EventHandler? OnConnect;
    public event EventHandler? OnDisconnected;

    /// <summary>
    /// Connects to the server
    /// </summary>
    /// <returns>A task that represents the asynchronous connecting operation</returns>
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
                Volatile.Write(ref activeTcpClient, tcpClient);
                wasConnected = true;
                OnConnect?.Invoke(this, EventArgs.Empty);
                await this.ReceiveDataLoopAsync(tcpClient.GetStream(), Logger, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Logger.LogInformation("Connection was stopped");
                break;
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                Logger.LogWarning("{error}", e.Message);
                Logger.LogInformation("Waiting {timeout} milliseconds to try again", ConnectSeparationTimeout);
                try { await Task.Delay(ConnectSeparationTimeout, cancellationToken); }
                catch (OperationCanceledException)
                {
                    Logger.LogInformation("Connection was stopped");
                    break;
                }
            }
            finally
            {
                Volatile.Write(ref activeTcpClient, null);
                if (wasConnected && !cancellationToken.IsCancellationRequested)
                    OnDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Sends data to the server
    /// </summary>
    /// <param name="data"></param>
    public async Task<bool> SendData(ReadOnlyMemory<byte> data)
    {
        var client = Volatile.Read(ref activeTcpClient);
        if (client is null or { Connected: false }) return false;
        var networkStream = client.GetStream();
        await networkStream.WriteAsync(data, ConnectionCancellationTokenSource.Token).ConfigureAwait(false);
        return true;
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
        }

        ConnectionCancellationTokenSource.Dispose();
        connectionSemaphore.Dispose();
    }
}