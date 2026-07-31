using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Logging.Interfaces;
using Microsoft.Extensions.Logging;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Enums;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemHelperClasses.Interfaces;
using SecsGemMessageHandling.Enums;

namespace SecsGemMessageHandling.Data_Handling;
public partial class DataMessageHandler : ObservableObject
{
    [ObservableProperty]
    public partial bool CommunicationsEstablished { get; set; }

    private readonly IHSMSParameters hsmsParameters;
    private readonly CommunicationHandler communicationHandler;
    private readonly ILogger<DataMessageHandler> logger;
    private readonly ControlStateInfo controlStateInfo;
    private readonly ControlMessageHandling controlMessageHandling;
    private readonly TransactionHandler transactionHandler;
    private readonly Timer commDelayTimer;
    private readonly ConcurrentQueue<(DateTime ReceivedAt, ILoggedDataMessage Message)> _recentMessages = new();
    public IEventBus<ILoggedDataMessage> OnMessageInCommunicationsEstablished { get; }
    private ControlSubstate ControlSubstate => controlStateInfo.ControlSubstate;
    private ControlState ControlState => controlStateInfo.ControlState;
    private CancellationToken Cancellation { get; set; }

    public DataMessageHandler(IHSMSParameters hsmsParameters,
        CommunicationHandler communicationHandler,
        ILogger<DataMessageHandler> logger,
        TransactionHandler transactionHandler,
        ControlStateInfo controlStateInfo,
        ControlMessageHandling controlMessageHandling,
        IEventBusFactory eventBusFactory)
    {
        this.hsmsParameters = hsmsParameters;
        this.communicationHandler = communicationHandler;
        this.logger = logger;
        this.transactionHandler = transactionHandler;
        this.controlStateInfo = controlStateInfo;
        this.controlMessageHandling = controlMessageHandling;
        OnMessageInCommunicationsEstablished = eventBusFactory.CreateTransient<ILoggedDataMessage>();

        this.communicationHandler.OnDataMessageIn.Subscribe(HandleReceivedMessage);
        this.controlMessageHandling.PropertyChanged += HsmsStatusChanged;
        this.communicationHandler.OnDisconnect += CommunicationHandler_OnDisconnect;

        commDelayTimer = new Timer(OnCommDelayTimerExpired, null, 
            Timeout.Infinite, Timeout.Infinite);
    }

    private void HsmsStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(controlMessageHandling.HSMSStatus) && !CommunicationsEstablished)
            StartCommDelayTimer();
    }

    /// <summary>
    /// Starts the communication delay timer with the configured delay interval.
    /// </summary>
    /// <remarks>
    /// The timer is initialized to trigger at with a delay depending on
    /// <see cref="HSMSParameters.CommunicationsWaitDelay"/>
    /// </remarks>
    private void StartCommDelayTimer()
    {
        commDelayTimer.Change(new TimeSpan(0, 0, 0, 0, (int)hsmsParameters.CommunicationsWaitDelay),
            Timeout.InfiniteTimeSpan);
    }

    private void StopCommDelayTimer()
    {
        commDelayTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private async Task<bool> SendCommunicationsRequestAsync()
    {
        var communicationsRequest = new MessageFactory(1, 13, true, "Establish Communications Request",
                new ItemFactory()
                    .AddList(l1 => l1
                        .AddAscii("MDLN TEST", "MDLN")
                        .AddAscii("SOFTREV TEST", "SOFTREV")))
            .Build();

        var (error, reply) = await SendDataMessage(communicationsRequest, Cancellation);

        if (error != TransactionHandlerError.None)
            return false;

        if (reply?.Data is not { Stream: 1, Function: 14, Reply: false })
            return false;

        var commack = reply.Data[0]?[0];
        if (commack == null)
            return false;

        if (!commack.CheckValue(SecsGemItemFormatType.Binary, ["00"]))
        {
            logger.LogError("Communications denied by equipment (COMMACK != 0). Disconnecting.");
            communicationHandler.RestartConnection();
            return true;
        }

        OnCommunicationsEstablished();
        return true;
    }

    private async void OnCommDelayTimerExpired(object? _)
    {
        try
        {
            await OnCommDelayTimerExpired();
        }
        catch (Exception e)
        {
            logger.LogCritical("Unhandled exception inside OnCommDelayTimerExpired: {exception}", e.Message);
        }
    }

    private async Task OnCommDelayTimerExpired()
    {
        if (!await SendCommunicationsRequestAsync())
        {
            StartCommDelayTimer();
        }
    }

    public async Task<(TransactionHandlerError error, ILoggedDataMessage? reply)> SendDataMessage(SecsGemDataMessage message, CancellationToken cancellation = default) =>
        await SendDataMessage(message, transactionHandler.CreateSystemBytes(), cancellation).ConfigureAwait(false);

    public async Task<(TransactionHandlerError error, ILoggedDataMessage? reply)> SendDataMessage(SecsGemDataMessage message, uint systemBytes, CancellationToken _ = default)
    {
        if (!CanSendMessage(message))
        {
            return (TransactionHandlerError.CantSendMessage, null);
        }

        if (message.Reply)
        {
            return await transactionHandler.SendTransaction(message).ConfigureAwait(false);
        }

        await communicationHandler.SendAndLogMessage(message, systemBytes);
        return (TransactionHandlerError.DoesNotRequireAReply, null);
    }

    public bool CanSendMessage(SecsGemDataMessage message)
    {
        if (hsmsParameters.IgnoreState)
            return true;
        if (!hsmsParameters.CommunicationsEnabled)
        {
            logger.LogInformation("{messageName} cannot be sent, SECS II communications are not enabled", message.Name);
            return false;
        }
        if (controlMessageHandling.HSMSStatus != HSMSStatus.Selected)
        {
            logger.LogInformation(
                "{messageName} cannot be sent, SECS II communications are not enabled, HSMS status currently set at {status}",
                message.Name,
                controlMessageHandling.HSMSStatus);
            return false;
        }
        if (message is { Stream: 1, Function: 13, Reply: true } or { Stream: 1, Function: 14, Reply: false } or { Stream: 9 })
        {
            return true;
        }
        if (!CommunicationsEstablished)
        {
            logger.LogInformation("{messageName} cannot be sent, SECS II communications are not established", message.Name);
            return false;
        }

        if (ControlState == ControlState.Offline)
        {
            if (message is { Stream: 1, Function: 17, Reply: true } or { Stream: 1, Function: 18, Reply: false })
            {
                return true;
            }

            if (ControlSubstate == ControlSubstate.AttemptOnline && message is { Stream: 1, Function: 1 })
            {
                return true;
            }

            logger.LogInformation("{messageName} cannot be sent, control state is offline", message.Name);
            return false;
        }

        return true;
    }

    public bool CanReceiveMessage(SecsGemDataMessage message)
    {
        if (hsmsParameters.IgnoreState)
            return true;
        if (!hsmsParameters.CommunicationsEnabled)
        {
            logger.LogInformation("{messageName} cannot be received, SECS II communications are not enabled", message.Name);
            return false;
        }
        if (controlMessageHandling.HSMSStatus != HSMSStatus.Selected)
        {
            logger.LogInformation(
                "{messageName} cannot be received, SECS II communications are not enabled, HSMS status currently set at {status}",
                message.Name,
                controlMessageHandling.HSMSStatus);
            return false;
        }
        if (!CommunicationsEstablished)
        {
            if (message is { Stream: 1, Function: 13 } or { Stream: 9 } or { Stream: 1, Function: 14 })
            {
                return true;
            }
            logger.LogInformation("{messageName} cannot be received, SECS II communications are not established", message.Name);
            return false;
        }

        if (ControlState == ControlState.Offline)
        {
            if (message is { Stream: 1, Function: 13 } or { Stream: 9 } or { Stream: 1, Function: 14 }
                or { Stream: 1, Function: 17 } or { Stream: 1, Function: 18 })
            {
                return true;
            }

            if (ControlSubstate == ControlSubstate.AttemptOnline && message is { Stream: 1, Function: 1 })
            {
                return true;
            }

            logger.LogInformation("{messageName} cannot be sent, control state is offline", message.Name);
            return false;
        }

        return true;
    }

    private static readonly TimeSpan MessageBufferWindow = TimeSpan.FromSeconds(5);

    private void BufferMessage(ILoggedDataMessage messageLog)
    {
        var cutoff = DateTime.UtcNow - MessageBufferWindow;
        _recentMessages.Enqueue((DateTime.UtcNow, messageLog));
        while (_recentMessages.TryPeek(out var oldest) && oldest.ReceivedAt < cutoff)
            _recentMessages.TryDequeue(out _);
    }

    private async Task HandleReceivedMessage(ILoggedDataMessage messageLog)
    {
        BufferMessage(messageLog);

        var message = messageLog.Data;
        var stream = message.Stream;
        var function = message.Function;

        if (!hsmsParameters.CommunicationsEnabled)
        {
            return;
        }

        switch (stream)
        {
            case 1:
                switch (function)
                {
                    case 13:
                        await HandleEstablishCommunicationsRequest(messageLog);
                        break;
                }
                break;
        }

        if (CommunicationsEstablished)
        {
            OnMessageInCommunicationsEstablished.Publish(messageLog);
        }
    }

    private async Task HandleEstablishCommunicationsRequest(ILoggedDataMessage message)
    {
        //TODO: handle these 2 somehow
        var mdln = message.Data[0]?[0];
        var softrev = message.Data[0]?[1];

        var communicationsAcknowledge = new MessageFactory(1,14,false, "Establish Communications Request Acknowledge", 
                new ItemFactory()
                    .AddList(l1 => l1
                        .AddBinary(0, "COMMACK")
                        .AddList(l2 => l2
                            .AddAscii("MDLN TEST", "MDLN")
                            .AddAscii("SOFTREV TEST", "SOFTREV"))))
            .Build();

        await SendDataMessage(communicationsAcknowledge, message.HeaderData.SystemBytes, Cancellation);

        OnCommunicationsEstablished();
    }

    private void OnCommunicationsEstablished()
    {
        StopCommDelayTimer();
        CommunicationsEstablished = true;
    }

    private void CommunicationHandler_OnDisconnect(object? sender, EventArgs e)
    {
        StopCommDelayTimer();
        CommunicationsEstablished = false;
    }

    public async Task<ILoggedDataMessage?> WaitForReceivedMessage(
        Func<SecsGemDataMessage, bool> predicate, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var tcs = new TaskCompletionSource<ILoggedDataMessage?>();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linkedCts.CancelAfter(timeout);

        using var reg = linkedCts.Token.Register(() => tcs.TrySetResult(null));

        IDisposable? subscription = null;
        subscription = communicationHandler.OnDataMessageIn.Subscribe(msg =>
        {
            if (predicate(msg.Data))
            {
                tcs.TrySetResult(msg);
                subscription?.Dispose();
            }
        });

        // Check messages that arrived before the subscription was set up
        var cutoff = DateTime.UtcNow - MessageBufferWindow;
        foreach (var (receivedAt, msg) in _recentMessages)
        {
            if (receivedAt >= cutoff && predicate(msg.Data))
            {
                tcs.TrySetResult(msg);
                break;
            }
        }

        try
        {
            var result = await tcs.Task.ConfigureAwait(false);
            return result;
        }
        finally
        {
            subscription?.Dispose();
        }
    }
}
