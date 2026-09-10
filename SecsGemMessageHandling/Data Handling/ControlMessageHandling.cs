using CommunityToolkit.Mvvm.ComponentModel;
using SecsGemBaseItems.Data_Containers.Header;
using SecsGemBaseItems.SecsGemParameters.Enums;
using SecsGemMessageHandling.Enums;
using Logging.Interfaces;
using Microsoft.Extensions.Logging;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemMessageHandling.Helpers;

namespace SecsGemMessageHandling.Data_Handling;
public partial class ControlMessageHandling : ObservableObject
{
    [ObservableProperty]
    public partial HSMSStatus HSMSStatus { get; private set; }
    private Timer Timer7 { get; }
    private CommunicationHandler CommunicationHandler { get; }
    private ILogger<ControlMessageHandling> Logger { get; }
    private IHSMSParameters HSMSParameters { get; }
    private CancellationToken Cancellation { get; set; }
    private ControlMessageFactory ControlMessageFactory { get; }
    private TransactionHandler TransactionHandler { get; }
    private readonly SemaphoreSlim hsmsStatusSemaphore = new(1, 1);
    public ControlMessageHandling(CommunicationHandler communicationHandler, ILogger<ControlMessageHandling> logger, IHSMSParameters hsmsParameters, ControlMessageFactory controlMessageFactory, TransactionHandler transactionHandler)
    {
        CommunicationHandler = communicationHandler;
        Logger = logger;
        HSMSParameters = hsmsParameters;
        ControlMessageFactory = controlMessageFactory;
        TransactionHandler = transactionHandler;

        CommunicationHandler.OnControlMessageIn.Subscribe(OnControlMessageIn);
        CommunicationHandler.OnConnect += (_,_) => _ = HSMSStateChanging(HSMSStatus.NotSelected);
        CommunicationHandler.OnDisconnect += (_, _) => _ = HSMSStateChanging(HSMSStatus.NotConnected);

        Timer7 = new Timer(delegate
        {
            if (HSMSParameters.IgnoreState) return;
            Logger.LogError("No select state reached, T7 Timeout: {T7} milliseconds.", HSMSParameters.T7);
            _ = HSMSStateChanging(HSMSStatus.CommunicationFailure);
        }, null, Timeout.Infinite, Timeout.Infinite);

        _ = HSMSStateChanging(HSMSStatus.NotConnected);
    }

    private async Task OnControlMessageIn(ILoggedControlMessage e)
    {
        if (e.HeaderData is not { } controlMessage) return;

        var sessionType = (SessionType)controlMessage.SessionType;
        if (!Enum.IsDefined(sessionType)) return;


        switch (sessionType)
        {
            case SessionType.SelectReq:
                await OnSelectRequest(controlMessage).ConfigureAwait(false);
                break;
            case SessionType.SelectRsp:
                break;
            case SessionType.DeselectReq:
                await OnDeselectRequest(controlMessage).ConfigureAwait(false);
                break;
            case SessionType.DeselectRsp:
                break;
            case SessionType.LinktestReq:
                await OnLinktestRequest(controlMessage).ConfigureAwait(false);
                break;
            case SessionType.LinktestRsp:
                break;
            case SessionType.RejectReq:
                break;
            case SessionType.SeparateReq:
                break;
        }
    }

    private async Task SendSelectRequest()
    {
        var selectRequest = ControlMessageFactory.CreateSelectRequest();
        var (error, reply) = await TransactionHandler.SendControlTransaction(selectRequest).ConfigureAwait(false);

        if (error != TransactionHandlerError.None)
        {
            Logger.LogError("Error receiving response for select request");
            return;
        }

        if (reply?.HeaderData.SessionType == (uint)SessionType.SelectRsp)
        {
            if (reply.HeaderData.HeaderByte3 is 0 or 1)
            {
                await HSMSStateChanging(HSMSStatus.Selected).ConfigureAwait(false);
            }
        }
    }

    public async Task SendDeselectRequest()
    {
        var selectRequest = ControlMessageFactory.CreateSelectRequest();
        var (error, reply) = await TransactionHandler.SendControlTransaction(selectRequest).ConfigureAwait(false);

        if (error != TransactionHandlerError.None)
        {
            Logger.LogError("Error receiving response for deselect request");
            CommunicationHandler.RestartConnection();
            return;
        }

        if (reply?.HeaderData.SessionType == (uint)SessionType.DeselectRsp)
        {
            await OnDeselectResponse(reply.HeaderData);
        }
    }

    private async Task OnDeselectResponse(HeaderData controlMessage)
    {
        if (controlMessage.HeaderByte3 is 0 or 1)
        {
            await HSMSStateChanging(HSMSStatus.NotSelected).ConfigureAwait(false);
        }
    }

    private async Task OnSelectRequest(HeaderData controlMessage)
    {
        SelectStatus selectStatus;
        switch (HSMSStatus)
        {
            case HSMSStatus.NotSelected:
                selectStatus = SelectStatus.CommunicationEstablished;
                await HSMSStateChanging(HSMSStatus.Selected).ConfigureAwait(false);
                break;
            case HSMSStatus.Selected:
                selectStatus = SelectStatus.CommunicationAlreadyActive;
                break;
            case HSMSStatus.NotConnected or HSMSStatus.CommunicationFailure:
                return;
            default:
                throw new InvalidOperationException($"Unexpected value: {nameof(HSMSStatus)}");
        }

        var selectResponse = 
            ControlMessageFactory.CreateSelectResponse(controlMessage.SystemBytes, controlMessage.DeviceId, selectStatus);

        await CommunicationHandler.SendStateMessage(selectResponse).ConfigureAwait(false);
    }

    private async Task OnDeselectRequest(HeaderData controlMessage)
    {
        DeselectStatus deselectStatus;
        switch (HSMSStatus)
        {
            case HSMSStatus.Selected:
                deselectStatus = DeselectStatus.CommunicationEnded;
                await HSMSStateChanging(HSMSStatus.NotSelected).ConfigureAwait(false);
                break;
            case HSMSStatus.NotSelected:
            case HSMSStatus.NotConnected:
            case HSMSStatus.CommunicationFailure:
                deselectStatus = DeselectStatus.CommunicationNotEstablished;
                break;
            default:
                throw new InvalidOperationException($"Unexpected value: {nameof(HSMSStatus)}");
        }

        var deselectResponse = 
            ControlMessageFactory.CreateDeselectResponse(controlMessage.SystemBytes, controlMessage.DeviceId, deselectStatus);

        await CommunicationHandler.SendStateMessage(deselectResponse).ConfigureAwait(false);
    }


    private async Task OnLinktestRequest(HeaderData controlMessage)
    {
        var linktestResponse = ControlMessageFactory.CreateLinktestResponse(controlMessage.SystemBytes);

        await CommunicationHandler.SendStateMessage(linktestResponse).ConfigureAwait(false);
    }

    private async Task HSMSStateChanging(HSMSStatus newState)
    {
        await hsmsStatusSemaphore.WaitAsync(Cancellation).ConfigureAwait(false);

        try
        {
            if (HSMSStatus == newState || (int)newState > (int)HSMSStatus + 1)
                return;
            
            HSMSStatus = newState;

            switch (HSMSStatus)
            {
                case HSMSStatus.Selected:
                    StopT7Timer();
                    Logger.LogInformation("On select state, stopping T7 Timer");
                    break;
                case HSMSStatus.NotSelected:
                    StartT7Timer();
                    if (HSMSParameters.InitiateSelectRequest)
                    {
                        hsmsStatusSemaphore.Release();
                        try
                        {
                            await SendSelectRequest().ConfigureAwait(false);
                        }
                        finally
                        {
                            await hsmsStatusSemaphore.WaitAsync(Cancellation).ConfigureAwait(false);
                        }
                    }
                    break;
                case HSMSStatus.NotConnected:
                    StopT7Timer();
                    break;
                case HSMSStatus.CommunicationFailure:
                    Logger.LogError("On Communication Failure");
                    CommunicationHandler.RestartConnection();
                    StopT7Timer();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(newState));
            }
        }
        finally
        {
            hsmsStatusSemaphore.Release();
        }
    }

    private void StopT7Timer()
    {
        Timer7.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void StartT7Timer()
    {
        if (HSMSParameters.IgnoreState)
        {
            Logger.LogInformation("HSMS State is ignored, T7 timer won't be started");
            return;
        }
        Logger.LogInformation("Start T7 Timer: {Timer} milliseconds", HSMSParameters.T7);
        Timer7.Change(HSMSParameters.T7, Timeout.Infinite);
    }
}
