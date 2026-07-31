using Logging.Interfaces;
using Microsoft.Extensions.Logging;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Interfaces;
using SecsGemBaseItems.Enums;
using System.Collections.ObjectModel;
using Logging;
using SecsGemHelperClasses.Interfaces;

namespace SecsGemMessageHandling.Data_Handling;


public class ControlStateHandler
{
    private readonly ControlStateInfo controlStateInfo;
    private readonly DataMessageHandler dataMessageHandler;
    private readonly ILogger<ControlStateHandler> logger;

    /// <summary>
    /// Messages will be forwarded through this bus only while the equipment is currently in online mode
    /// </summary>
    private readonly IEventBus<ILoggedControlStateDataMessage> messageBus;

    public IFilteredEventBus<ILoggedControlStateDataMessage> OnMessageInOffline { get; }
    public IFilteredEventBus<ILoggedControlStateDataMessage> OnMessageInOnlineLocal { get; }
    public IFilteredEventBus<ILoggedControlStateDataMessage> OnMessageInOnlineRemote { get; }
    private ControlSubstate ControlSubstate
    {
        get => controlStateInfo.ControlSubstate;
        set => controlStateInfo.ControlSubstate = value;
    }

    private ControlState ControlState
    {
        get => controlStateInfo.ControlState;
        set => controlStateInfo.ControlState = value;
    }

    public ControlStateHandler(DataMessageHandler dataMessageHandler,
        ILogger<ControlStateHandler> logger, 
        ControlStateInfo controlStateInfo,
        IEventBusFactory eventBusFactory)
    {
        this.dataMessageHandler = dataMessageHandler;
        this.logger = logger;
        this.controlStateInfo = controlStateInfo;
        messageBus = eventBusFactory.CreateTransient<ILoggedControlStateDataMessage>();

        OnMessageInOffline = 
            messageBus.Filter<ILoggedControlStateDataMessage>(message => message.ControlState == ControlState.Offline);
        OnMessageInOnlineLocal =
            messageBus.Filter<ILoggedControlStateDataMessage>(message =>
                message is { ControlState: ControlState.Online, ControlSubstate: ControlSubstate.OnlineLocal });
        OnMessageInOnlineRemote =
            messageBus.Filter<ILoggedControlStateDataMessage>(message =>
                message is { ControlState: ControlState.Online, ControlSubstate: ControlSubstate.OnlineRemote });

        dataMessageHandler.OnMessageInCommunicationsEstablished.Subscribe(HandleReceivedMessage);
    }
    private async Task HandleReceivedMessage(ILoggedDataMessage messageLog)
    {
        var message = messageLog.Data;
        var stream = message.Stream;
        var function = message.Function;

        switch (stream)
        {
            case 1:
                switch (function)
                {
                    case 15:
                        await controlStateInfo.RunWithLockAsync(async () =>
                            await HandleRequestOffline(messageLog).ConfigureAwait(false));
                        break;
                    case 17:
                        await controlStateInfo.RunWithLockAsync(async () => 
                            await HandleRequestOnline(messageLog).ConfigureAwait(false));
                        break;
                }
                break;
        }

        var messageLogForward = new LoggedControlStateDataMessage()
        {
            Message = messageLog,
            ControlState = this.ControlState,
            ControlSubstate = this.ControlSubstate
        };

        messageBus.Publish(messageLogForward);
    }

    private async Task HandleRequestOffline(ILoggedDataMessage messageLog)
    {
        var offlineAcknowledge = new MessageFactory(1, 16, false, "OFF-LINE Acknowledge",
            new ItemFactory().AddBinary(0, "OFLACK")).Build();

        switch (ControlState)
        {
            case ControlState.Offline:
                logger.LogInformation("Request OFF-LINE received, equipment currently not ON-LINE, no action required");
                break;
            case ControlState.Online:
                logger.LogInformation("Request OFF-LINE received, going into OFF-LINE state");
                break;
        }

        await dataMessageHandler.SendDataMessage(offlineAcknowledge, messageLog.HeaderData.SystemBytes);

        if (ControlState == ControlState.Online)
        {
            ControlState = ControlState.Offline;
            await ControlStateChanging(ControlSubstate.HostOffline).ConfigureAwait(false);
        }
    }

    private async Task HandleRequestOnline(ILoggedDataMessage messageLog)
    {
        var onlineAcknowledge = new MessageFactory(1, 18, false, "ON-LINE Acknowledge (ONLA)").Build();

        switch (ControlSubstate)
        {
            case ControlSubstate.HostOffline:
                logger.LogInformation("Request ON-LINE received, going into ON-LINE state");

                onlineAcknowledge.Children =
                    new ObservableCollection<IDataItem>(new ItemFactory().AddBinary(0, "ONLACK").Build());
                break;
            case ControlSubstate.AttemptOnline or ControlSubstate.EquipmentOffline:
                logger.LogInformation("Request ON-LINE received, currently at {substate} substate, switching to online not allowed", ControlSubstate);

                onlineAcknowledge.Children =
                    new ObservableCollection<IDataItem>(new ItemFactory().AddBinary(1, "ONLACK").Build());
                break;
            case ControlSubstate.OnlineLocal or ControlSubstate.OnlineRemote:
                logger.LogInformation("Request ON-LINE received, equipment is already online");

                onlineAcknowledge.Children =
                    new ObservableCollection<IDataItem>(new ItemFactory().AddBinary(2, "ONLACK").Build());
                break;
        }

        await dataMessageHandler.SendDataMessage(onlineAcknowledge, messageLog.HeaderData.SystemBytes).ConfigureAwait(false);

        if (ControlSubstate == ControlSubstate.HostOffline)
        {
            await EnterControlStateOnline();
        }
    }
    private async Task SendAreYouThereRequest()
    {
        var areYouThereRequest = new MessageFactory(1, 1, true, "Are You There Request (R)").Build();

        var (_, reply) = await dataMessageHandler.SendDataMessage(areYouThereRequest);

        switch (reply?.Data)
        {
            case { Stream: 1, Function: 2, Reply: false }:
                // TODO: check metadata?
                await EnterControlStateOnline();
                break;
            case { Stream: 1, Function: 0, Reply: false }:
            default:
                await ControlStateChanging(controlStateInfo.DefaultFailedOnlineAttempt);
                break;
        }
    }

    private async Task EnterControlStateOnline()
    {
        if (ControlSubstate == ControlSubstate.EquipmentOffline)
        {
            logger.LogWarning("Can't transition to ON-LINE state, transition directly from {substate} substate is not possible", ControlSubstate.EquipmentOffline);
            return;
        }
        if (ControlState == ControlState.Offline)
        {
            ControlState = ControlState.Online;

            await ControlStateChanging(controlStateInfo.OnlineSubstate).ConfigureAwait(false);
        }
    }

    private async Task ControlStateChanging(ControlSubstate newState)
    {
        if (ControlSubstate == newState)
            return;

        ControlSubstate = newState;

        switch (newState)
        {
            case ControlSubstate.EquipmentOffline:
                break;
            case ControlSubstate.AttemptOnline:
                await SendAreYouThereRequest();
                break;
            case ControlSubstate.HostOffline:
            case ControlSubstate.OnlineLocal:
            case ControlSubstate.OnlineRemote:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(newState), newState, null);
        }
    }

    public async Task TurnOnlineSwitch()
    {
        await controlStateInfo.RunWithLockAsync(async () =>
        {
            if (ControlSubstate == ControlSubstate.EquipmentOffline)
                await ControlStateChanging(ControlSubstate.AttemptOnline).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task TurnOfflineSwitch()
    {
        await controlStateInfo.RunWithLockAsync(async () =>
        {
            if (ControlSubstate is not ControlSubstate.EquipmentOffline and not ControlSubstate.AttemptOnline)
            {
                ControlState = ControlState.Offline;
                await ControlStateChanging(ControlSubstate.EquipmentOffline).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    public async Task TurnLocalSwitch()
    {
        await controlStateInfo.RunWithLockAsync(async () =>
        {
            controlStateInfo.OnlineSubstate = ControlSubstate.OnlineLocal;

            if (ControlState == ControlState.Online)
            {
                await ControlStateChanging(ControlSubstate.OnlineLocal);
            }
        }).ConfigureAwait(false);
    }

    public async Task TurnRemoteSwitch()
    {
        await controlStateInfo.RunWithLockAsync(async () =>
        {
            controlStateInfo.OnlineSubstate = ControlSubstate.OnlineRemote;

            if (ControlState == ControlState.Online)
            {
                await ControlStateChanging(ControlSubstate.OnlineRemote);
            }
        }).ConfigureAwait(false);
    }
}


