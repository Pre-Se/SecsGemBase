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
    /// Starts listening and accepts a client connection
    /// </summary>
    /// <returns>A task that represents the asynchronous listening operation</returns>
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
            connectionSemaphore.Release();
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
            TcpListener.Stop();
            OnConnect?.Invoke(this, EventArgs.Empty);

            await this.ReceiveDataLoopAsync(ConnectedClient.GetStream(), Logger, ConnectionCancellationTokenSource.Token);
        }
        catch (SocketException e)
        {
            Logger.LogWarning("{error}", e.Message);
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("Stopped listening for incoming connections");
        }
    }

    /// <summary>
    /// Sends data to the connected client
    /// </summary>
    public async Task<bool> SendData(ReadOnlyMemory<byte> data)
    {
        var networkStream = ConnectedClient?.GetStream();

        if (networkStream == null) return false;

        await networkStream.WriteAsync(data).ConfigureAwait(false);
        return true;
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
        }

        ConnectionCancellationTokenSource.Dispose();
        TcpListener.Stop();
        TcpListener.Dispose();
        if (ConnectedClient != null)
        {
            ConnectedClient.Close();
            ConnectedClient.Dispose();
        }
        connectionSemaphore.Dispose();
    }
}
