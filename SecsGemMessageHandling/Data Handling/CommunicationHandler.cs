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
    private ITCPIPBase TCPIPBase { get; set; }


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
    ///     Event that is raised when a connection is established
    /// </summary>
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
            Task.Run(ConnectLoopAsync);
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
            ConnectionOn = false;
            DisposeConnection();
            BuildConnectionBase();
        }
        finally
        {
            connectionStatusSemaphore.Release();
        }
    }

    public void RestartConnection()
    {
        connectionStatusSemaphore.Wait();
        try
        {
            if (ConnectionStatus is ConnectionStatus.Connected)
                logger.LogInformation("Communication will be restarted");
            DisposeConnection();
            BuildConnectionBase();
            if (ConnectionOn)
            {
                ConnectionStatus = ConnectionStatus.PortOpen;
                Task.Run(ConnectLoopAsync);
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
        catch (Exception e) when (e is SocketException or IOException or InvalidOperationException)
        {
            if (!SecsGemSessionType.TryGetSessionType(data.SessionType, out var messageType))
                messageType = "unknown message type";
            logger.LogError("Error sending {Type} : {Message}, remote entity was no longer connected", messageType, e.Message);
            status = MessageStatus.Failure;
        }
        
        var log = new LoggedControlMessage(data, message, MessageResult.Sent)
        {
            Status = status
        };

        switch (status)
        {
            case MessageStatus.Failure:
                RestartConnection();
                break;
            case MessageStatus.Success:
                messageBus.Publish(log);
                break;
        }

        return log;
    }
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

    private async Task ConnectLoopAsync()
    {
        try
        {
            await TCPIPBase.Connect();
        }
        catch (Exception ex)
        {
            logger.LogError("Unhandled exception: {Exception}", ex.Message);
            ClosePort();
        }
    }

    /// <summary>
    ///     Stops and closes all communication with the remote device
    /// </summary>
    private void DisposeConnection()
    {
        TCPIPBase.OnConnect -= OnConnected;
        TCPIPBase.OnDisconnected -= RestartConnection;
        TCPIPBase.Dispose();
    }
    /// <summary>
    ///     Sends and logs a <see cref="SecsGemDataMessage" /> to the remote device
    /// </summary>
    private async Task<(MessageStatus status,byte[] rawData, HeaderData header)> SendDataMessage(SecsGemDataMessage message, uint systemBytes)
    {
        var (rawData, header) = await Task.Run(() => BuildMessageData(message, systemBytes)).ConfigureAwait(false);

        var status = MessageStatus.Pending;
        try
        {
            if (await SendDataAsync(rawData).ConfigureAwait(false))
            {
                status = MessageStatus.Success;
            }
            else
            {
                status = MessageStatus.Failure;
                logger.LogError("Error sending {Message}", message.Name);
            }
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            logger.LogError("Error sending {Message} : {Error}", message.Name, e.Message);
        }

        if (status != MessageStatus.Failure) return (MessageStatus.Success, rawData, header);

        RestartConnection();
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
        TCPIPBase = CreateNewConnectionBase();
        TCPIPBase.OnDataReceived.Subscribe(ParseDataReceived);
        TCPIPBase.OnConnect += OnConnected;
        TCPIPBase.OnDisconnected += RestartConnection;
        if (TCPIPBase is TCPIPClientBase clientBase) clientBase.ConnectSeparationTimeout = (int)hsmsParameters.T5;

        return TCPIPBase;
    }

    private void RestartConnection(object? sender, EventArgs e)
    {
        if (Interlocked.CompareExchange(ref restartPending, 1, 0) == 0)
            Task.Run(() =>
            {
                RestartConnection();
                Interlocked.Exchange(ref restartPending, 0);
            });
    }

    private void OnConnected(object? sender, EventArgs eventArgs)
    {
        connectionStatusSemaphore.Wait();
        try
        {
            if (!ConnectionOn) return;
            OnConnect?.Invoke(this, EventArgs.Empty);
            ConnectionStatus = ConnectionStatus.Connected;
        }
        finally
        {
            connectionStatusSemaphore.Release();
        }
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