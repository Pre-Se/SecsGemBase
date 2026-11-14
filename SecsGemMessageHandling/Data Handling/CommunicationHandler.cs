using System.ComponentModel;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using Logging;
using Logging.Enums;
using Logging.Interfaces;
using Microsoft.Extensions.Logging;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Header;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemBaseItems.SecsGemParameters.Enums;
using SecsGemHelperClasses;
using SecsGemHelperClasses.Interfaces;
using SecsGemMessageHandling.Enums;
using SecsGemMessageHandling.Helpers;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.TCPBase;

namespace SecsGemMessageHandling.Data_Handling;

/// <summary>
///     Handles incoming and outgoing communication with another SECS/GEM device
/// </summary>
public partial class CommunicationHandler : ObservableObject, IDisposable
{
    private readonly SemaphoreSlim connectionStatusSemaphore = new(1, 1);
    private int restartPending = 0;

    /// <summary>
    /// true if a connection is currently active or requested, false otherwise
    /// Setting this property also updates <see cref="ConnectionStatus"/> to keep both in sync.
    /// </summary>
    public bool ConnectionOn
    {
        get;
        private set
        {
            field = value;
            ConnectionStatus = value ? ConnectionStatus.PortOpen : ConnectionStatus.PortClosed;
        }
    }

    /// <summary>
    ///     Gets the current connection status of the underlying TCPIP connection/>
    /// </summary>
    [ObservableProperty] public partial ConnectionStatus ConnectionStatus { get; private set; }

    /// <summary>
    ///     Initializes a new instance of the <see cref="CommunicationHandler" /> class
    /// </summary>
    public CommunicationHandler(INetworkSettings networkSettings,
        INetworkConnectionFactory connectionFactory, ILogger<CommunicationHandler> logger, 
        IHSMSParameters hsmsParameters, ControlMessageFactory controlMessageFactory,
        IEventBusFactory eventBusFactory)
    {
        this.connectionFactory = connectionFactory;
        this.logger = logger;
        this.hsmsParameters = hsmsParameters;
        this.controlMessageFactory = controlMessageFactory;
        this.networkSettings = networkSettings;
        TCPIPBase = BuildConnectionBase();
        this.networkSettings.PropertyChanged += RestartConnection;
        this.hsmsParameters.PropertyChanged += UpdateConnectionProperties;

        messageBus = eventBusFactory.CreateTransient<ILoggedSecsGemMessage>();

        OnRawMessageIn = eventBusFactory.CreateTransient<ILoggedSecsGemMessage>();
        OnDataMessageIn = messageBus.Filter<ILoggedDataMessage>((message => message.Result == MessageResult.Received));
        OnDataMessageOut = messageBus.Filter<ILoggedDataMessage>((message => message.Result == MessageResult.Sent));
        OnControlMessageIn = messageBus.Filter<ILoggedControlMessage>((message => message.Result == MessageResult.Received));
        OnControlMessageOut = messageBus.Filter<ILoggedControlMessage>((message => message.Result == MessageResult.Sent));
    }

    private readonly IEventBus<ILoggedSecsGemMessage> messageBus;

    /// <summary>
    ///     Event that is raised when message is received from the remote device, before parsing.
    /// </summary>
    public IEventBus<ILoggedSecsGemMessage> OnRawMessageIn { get; }
    
    /// <summary>
    ///     Event that is raised when a <see cref="SecsGemDataMessage" /> is received from the remote device.
    /// </summary>
    public IFilteredEventBus<ILoggedDataMessage> OnDataMessageIn { get; }

    /// <summary>
    ///     Event that is raised when a <see cref="SecsGemDataMessage" /> is sent to the remote device.
    /// </summary>
    public IFilteredEventBus<ILoggedDataMessage> OnDataMessageOut { get; }

    /// <summary>
    ///     Event that is raised when a control message is received from the remote device.
    /// </summary>
    public IFilteredEventBus<ILoggedControlMessage> OnControlMessageIn { get; }

    /// <summary>
    ///     Event that is raised when a control message is sent to the remote device.
    /// </summary>
    public IFilteredEventBus<ILoggedControlMessage> OnControlMessageOut { get; }

    private readonly IHSMSParameters hsmsParameters;
    private readonly INetworkSettings networkSettings;
    private readonly INetworkConnectionFactory connectionFactory;
    private readonly ILogger<CommunicationHandler> logger;
    private readonly ControlMessageFactory controlMessageFactory;
    // Volatile: OnConnected reads it on the connection's loop without holding the lock.
    private ITCPIPBase TCPIPBase
    {
        get => Volatile.Read(ref tcpipBase);
        set => Volatile.Write(ref tcpipBase, value);
    }

    private ITCPIPBase tcpipBase = null!;

    /// <summary>
    ///     Cancelled by <see cref="DisposeConnection"/> before it disposes the current connection. A late OnConnected
    ///     of that connection then stops waiting for the lock: the disposing thread holds it while Dispose() waits up
    ///     to 5 s for the very loop that raised OnConnected. Replaced in <see cref="BuildConnectionBase"/>.
    /// </summary>
    private CancellationTokenSource connectionDisposing = new();

    /// <summary>The handler subscribed to the current connection's OnConnect, bound to that connection object.</summary>
    private EventHandler? connectionOnConnect;


    private CancellationTokenSource ConnectionCancellationTokenSource { get; set; } = new();

    public void Dispose()
    {
        DisposeConnection();
        messageBus.Dispose();
        ConnectionCancellationTokenSource.Cancel();
        ConnectionCancellationTokenSource.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Event that is raised when a connection is established, after <see cref="ConnectionStatus"/> has become
    ///     <see cref="Enums.ConnectionStatus.Connected"/>.
    /// </summary>
    /// <remarks>
    ///     Raised synchronously on the connection's receive loop, before it reads the first message, and outside the
    ///     connection lock. Handlers should not block: <see cref="ClosePort"/> or <see cref="RestartConnection()"/>
    ///     called directly from a handler waits up to 5 s for this very loop; start them with Task.Run instead.
    /// </remarks>
    public event EventHandler? OnConnect;

    /// <summary>
    ///     Event that is raised when disconnecting from remote device
    /// </summary>
    public event EventHandler? OnDisconnect;

    /// <summary>
    ///     Opens the connection to the remote device
    /// </summary>
    public void OpenPort()
    {
        connectionStatusSemaphore.Wait();
        try
        {
            ConnectionOn = true;
            var connection = TCPIPBase;
            Task.Run(() => ConnectLoopAsync(connection));
        }
        finally
        {
            connectionStatusSemaphore.Release();
        }
    }
    public void ClosePort()
    {
        connectionStatusSemaphore.Wait();
        try
        {
            DisposeConnection();
            BuildConnectionBase();
            // Status last, like RestartConnection: OnDisconnect then follows the OnConnect handlers that the old
            // connection's loop may still be running (Dispose waited for that loop).
            ConnectionOn = false;
        }
        finally
        {
            connectionStatusSemaphore.Release();
        }
    }

    public void RestartConnection() => RestartConnection(afterDispose: null);

    /// <param name="afterDispose">Runs under the lock once the old connection is disposed and unsubscribed, before
    /// the new one is built.</param>
    private void RestartConnection(Action? afterDispose)
    {
        connectionStatusSemaphore.Wait();
        try
        {
            if (ConnectionStatus is ConnectionStatus.Connected)
                logger.LogInformation("Communication will be restarted");
            DisposeConnection();
            afterDispose?.Invoke();
            BuildConnectionBase();
            if (ConnectionOn)
            {
                ConnectionStatus = ConnectionStatus.PortOpen;
                var connection = TCPIPBase;
                Task.Run(() => ConnectLoopAsync(connection));
            }
            else
            {
                ConnectionStatus = ConnectionStatus.PortClosed;
            }
        }
        finally
        {
            connectionStatusSemaphore.Release();
        }
    }
    /// <summary>
    ///     Sends a data message and, if it was written, publishes it on <see cref="OnDataMessageOut"/>.
    /// </summary>
    /// <returns>
    ///     The log entry. Its status is <see cref="MessageStatus.Failure"/> if the message could not be written; write
    ///     errors are not thrown. A failure queues a restart of the connection unless the connection was already being
    ///     closed.
    /// </returns>
    public async Task<ILoggedDataMessage> SendAndLogMessage(SecsGemDataMessage message, uint systemBytes)
    {
        var (status, rawData, header) = await SendDataMessage(message, systemBytes).ConfigureAwait(false);

        LoggedDataMessage log = new(header, (SecsGemDataMessage)message.Clone(), rawData, MessageResult.Sent)
        {
            Status = status
        };

        if (status == MessageStatus.Success)
            messageBus.Publish(log);

        return log;
    }
    /// <summary>
    ///     Sends a control message and, if it was written, publishes it on <see cref="OnControlMessageOut"/>.
    /// </summary>
    /// <returns>The log entry; for failures see <see cref="SendAndLogMessage"/>.</returns>
    public async Task<ILoggedControlMessage> SendStateMessage(HeaderData data)
    {
        var message = data.ToBytes();
        MessageStatus status;

        try
        {
            if (await SendDataAsync(message))
            {
                status = MessageStatus.Success;
            }
            else
            {
                logger.LogError("{Message} could not be sent", data.SessionType);
                status = MessageStatus.Failure;
            }
        }
        catch (OperationCanceledException)
        {
            // The connection is being disposed: no further restart, see SendDataMessage.
            logger.LogWarning("{Type} not sent, the connection is being closed", SessionTypeName(data));
            return new LoggedControlMessage(data, message, MessageResult.Sent) { Status = MessageStatus.Failure };
        }
        catch (Exception e) when (e is SocketException or IOException or InvalidOperationException)
        {
            logger.LogError("Error sending {Type} : {Message}, remote entity was no longer connected",
                SessionTypeName(data), e.Message);
            status = MessageStatus.Failure;
        }
        
        var log = new LoggedControlMessage(data, message, MessageResult.Sent)
        {
            Status = status
        };

        switch (status)
        {
            case MessageStatus.Failure:
                // Queued, not inline: the caller may be an OnConnect handler on the connection's own loop
                // (Select.req), and Dispose() of that connection waits for this very loop.
                RestartConnection(this, EventArgs.Empty);
                break;
            case MessageStatus.Success:
                messageBus.Publish(log);
                break;
        }

        return log;
    }

    private static string SessionTypeName(HeaderData data) =>
        SecsGemSessionType.TryGetSessionType(data.SessionType, out var name) ? name! : "unknown message type";

    /// <summary>
    ///     Sends data to the remote device
    /// </summary>
    private async Task<bool> SendDataAsync(ReadOnlyMemory<byte> message)
    {
        return await TCPIPBase.SendData(message).ConfigureAwait(false);
    }

    partial void OnConnectionStatusChanged(ConnectionStatus oldValue, ConnectionStatus newValue)
    {
        if (oldValue != ConnectionStatus && oldValue == ConnectionStatus.Connected)
            OnDisconnect?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateConnectionProperties(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IHSMSParameters.T5)) return;
        if (TCPIPBase is TCPIPClientBase clientBase) clientBase.ConnectSeparationTimeout = (int)hsmsParameters.T5;
    }

    private async Task ConnectLoopAsync(ITCPIPBase connection)
    {
        try
        {
            await connection.Connect().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError("Unhandled exception: {Exception}", ex.Message);
            ClosePortIfCurrent(connection);
        }
    }

    // The loop of a connection that RestartConnection/ClosePort has already replaced must not close its successor.
    // Checked under the lock: RestartConnection holds it from disposing the old connection until the new one is set.
    private void ClosePortIfCurrent(ITCPIPBase connection)
    {
        connectionStatusSemaphore.Wait();
        try
        {
            if (!ReferenceEquals(connection, TCPIPBase)) return;
            DisposeConnection();
            BuildConnectionBase();
            ConnectionOn = false; // last, see ClosePort
        }
        finally
        {
            connectionStatusSemaphore.Release();
        }
    }

    /// <summary>
    ///     Stops and closes all communication with the remote device
    /// </summary>
    private void DisposeConnection()
    {
        Volatile.Read(ref connectionDisposing).Cancel();
        TCPIPBase.OnConnect -= connectionOnConnect;
        TCPIPBase.OnDisconnected -= RestartConnection;
        TCPIPBase.Dispose();
    }
    /// <summary>
    ///     Sends and logs a <see cref="SecsGemDataMessage" /> to the remote device
    /// </summary>
    private async Task<(MessageStatus status,byte[] rawData, HeaderData header)> SendDataMessage(SecsGemDataMessage message, uint systemBytes)
    {
        var (rawData, header) = await Task.Run(() => BuildMessageData(message, systemBytes)).ConfigureAwait(false);

        // Every write error is a failure, as in SendStateMessage. Until GitLab secsgem/secsgembase#14 an IOException
        // was reported as Success, and other exceptions reached the caller.
        try
        {
            if (await SendDataAsync(rawData).ConfigureAwait(false))
                return (MessageStatus.Success, rawData, header);

            logger.LogError("Error sending {Message}", message.Name);
        }
        catch (OperationCanceledException)
        {
            // The connection is being disposed: a restart or ClosePort is already replacing it (see ITCPIPBase.SendData).
            logger.LogWarning("{Message} not sent, the connection is being closed", message.Name);
            return (MessageStatus.Failure, rawData, header);
        }
        catch (Exception e) when (e is SocketException or IOException or InvalidOperationException)
        {
            logger.LogError("Error sending {Message} : {Error}", message.Name, e.Message);
        }

        // Queued, not inline, like in SendStateMessage.
        RestartConnection(this, EventArgs.Empty);
        return (MessageStatus.Failure, rawData, header);
    }

    public (byte[] rawData, HeaderData header) BuildMessageData(SecsGemDataMessage message, uint systemBytes)
    {
        var header = controlMessageFactory.CreateDataMessageHeader(message, systemBytes);
        var messageBytes = message.ToBytes();
        header.SetDataMessageLength((uint)messageBytes.Length);

        var headerBytes = header.ToBytes();
        var rawBytes = MessageHandlingHelpers.Combine(headerBytes.ToArray(), messageBytes.ToArray());

        return (rawBytes, header);
    }

    private void ParseDataReceived(ReadOnlyMemory<byte> data)
    {
        var log = new LoggedSecsGemMessage(data, MessageResult.Received);
        OnRawMessageIn.Publish(log);

        if (!HeaderData.DecodeHeader(data, out var header))
        {
            logger.LogWarning("Message with invalid header received");
            return;
        }

        if (header.MessageLength <= data.Length - 4)
        {
            var message = new byte[header.MessageLength + 4];

            var dataSpan = data.Span;
            dataSpan[..(int)(header.MessageLength + 4)].CopyTo(message);

            var sessionType = (SessionType)header.SessionType;
            if (!Enum.IsDefined(sessionType)) return;

            switch (sessionType)
            {
                case SessionType.DataMessage:
                    var parsedMessage = ParseDataMessage(header, message);
                    var loggedDataMessage = new LoggedDataMessage(log, header, parsedMessage)
                    {
                        Status = MessageStatus.Success
                    };
                    messageBus.Publish(loggedDataMessage);
                    break;
                case SessionType.SelectReq:
                case SessionType.SelectRsp:
                case SessionType.DeselectReq:
                case SessionType.DeselectRsp:
                case SessionType.LinktestReq:
                case SessionType.LinktestRsp:
                case SessionType.RejectReq:
                case SessionType.SeparateReq:
                    var loggedControlMessage = new LoggedControlMessage(log, header)
                    {
                        Status = MessageStatus.Success
                    };
                    messageBus.Publish(loggedControlMessage);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected value: {nameof(sessionType)}");
            }
        }

        else
        {
            logger.LogError("Invalid session type: {SessionType} received in Message ID: {SystemBytes}", header.SessionType, header.SystemBytes);
        }
    }

    private ITCPIPBase BuildConnectionBase()
    {
        var connection = CreateNewConnectionBase();
        TCPIPBase = connection;
        connection.OnDataReceived.Subscribe(ParseDataReceived);
        // Bound to this object rather than taken from the event's sender: an ITCPIPBase decorator may pass on the
        // sender of the connection it wraps.
        connectionOnConnect = (_, _) => OnConnected(connection);
        connection.OnConnect += connectionOnConnect;
        connection.OnDisconnected += RestartConnection;
        if (connection is TCPIPClientBase clientBase) clientBase.ConnectSeparationTimeout = (int)hsmsParameters.T5;
        // Last, after TCPIPBase: OnConnected reads the token first and the connection second (see there).
        Volatile.Write(ref connectionDisposing, new CancellationTokenSource());

        return connection;
    }

    /// <summary>
    ///     Restarts the connection on the thread pool. Used for OnDisconnected, failed sends and network settings
    ///     changes, which may come from a connection's own loop or from a thread that holds
    ///     <see cref="connectionStatusSemaphore"/>. Requests that arrive while a restart is pending, up to the moment
    ///     it has disposed the old connection, are covered by that restart and dropped (the loss of a connection
    ///     often brings OnDisconnected and failed sends together; NetworkSettings.CopyFrom raises three
    ///     PropertyChanged). Requests after that queue a new restart: right for OnDisconnected of the new connection and
    ///     for newer settings, but also for a failed send on the old connection that arrives late, because requests are
    ///     not yet bound to their connection (GitLab secsgem/secsgembase#32).
    /// </summary>
    private void RestartConnection(object? sender, EventArgs e)
    {
        if (Interlocked.CompareExchange(ref restartPending, 1, 0) != 0) return;
        Task.Run(() =>
        {
            var cleared = false;
            try
            {
                // Cleared under the lock, right after the old connection is gone and before the new one exists.
                // Clearing only after RestartConnection() returned dropped an OnDisconnected of the new connection
                // that came in between, and nothing reconnected (TCPIPClientBase no longer reconnects by itself).
                RestartConnection(afterDispose: () =>
                {
                    Interlocked.Exchange(ref restartPending, 0);
                    cleared = true;
                });
            }
            catch (Exception ex)
            {
                logger.LogError("Restarting the connection failed: {Error}", ex.Message);
            }
            finally
            {
                // A restart that threw before disposing must not block every later restart.
                if (!cleared) Interlocked.Exchange(ref restartPending, 0);
            }
        });
    }

    private void OnConnected(ITCPIPBase connection)
    {
        // Token first, connection second: if the token is already the successor's, TCPIPBase is the successor too and
        // the check below drops this report. Otherwise the token is this connection's own, and RestartConnection or
        // ClosePort cancels it, while holding the lock, before Dispose() starts waiting for this loop.
        var disposing = Volatile.Read(ref connectionDisposing).Token;
        if (!ReferenceEquals(connection, TCPIPBase))
        {
            logger.LogDebug("Ignoring the connect of a connection that has already been replaced");
            return;
        }

        try
        {
            connectionStatusSemaphore.Wait(disposing);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Ignoring the connect of a connection that is being closed");
            return;
        }

        try
        {
            // A connection that RestartConnection/ClosePort has already replaced may still report its connect:
            // its loop read the delegate before DisposeConnection unsubscribed it.
            if (!ConnectionOn || !ReferenceEquals(connection, TCPIPBase)) return;
            ConnectionStatus = ConnectionStatus.Connected;
        }
        finally
        {
            connectionStatusSemaphore.Release();
        }

        // Outside the lock: subscribers send (ControlMessageHandling: Select.req) and may reach RestartConnection or
        // ClosePort synchronously, and SemaphoreSlim is not reentrant. This check is not atomic with the handlers: a
        // restart can still overlap them (e.g. when Dispose() did not wait for this loop because its first
        // ConnectAsync/Accept completed synchronously). ControlMessageHandling therefore only applies NotSelected
        // while the connection is Connected.
        if (!ReferenceEquals(connection, TCPIPBase)) return;
        OnConnect?.Invoke(this, EventArgs.Empty);
    }

    private ITCPIPBase CreateNewConnectionBase()
    {
        return connectionFactory.CreateTCPIPConnection(networkSettings.ConnectionMode);
    }

    private static SecsGemDataMessage ParseDataMessage(HeaderData header, byte[] message)
    {
        SecsGemDataMessage receivedMessage = new();
        receivedMessage.GetDataFromHeader(header);
        MessageParsing messageParsing = new(message);
        messageParsing.GetItems(receivedMessage);
        return receivedMessage;
    }
}