using Logging.Interfaces;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Header;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemBaseItems.SecsGemParameters.Enums;
using SecsGemHelperClasses;
using System.Collections.Concurrent;
using Logging.Enums;
using Microsoft.Extensions.Logging;
using SecsGemHelperClasses.Interfaces;
using SecsGemMessageHandling.Enums;

namespace SecsGemMessageHandling.Data_Handling;
public class TransactionHandler
{
    public TransactionHandler(IHSMSParameters hsmsParameters, ILogger<TransactionHandler> logger, CommunicationHandler communicationHandler, IEventBusFactory eventBusFactory)
    {
        HsmsParameters = hsmsParameters;
        Logger = logger;
        CommunicationHandler = communicationHandler;

        OnTransactionMatched = eventBusFactory.CreateTransient<ILoggedTransaction>();
        CommunicationHandler.OnDataMessageIn.Subscribe(TryHandleDataMessageReply);
        CommunicationHandler.OnControlMessageIn.Subscribe(TryHandleControlMessageReply);
        CommunicationHandler.OnDisconnect += RemoveAllTransactions;
    }

    /// <summary>
    /// Contains sent transactions with pending replies
    /// where the key (unsigned integer) are the system bytes of the primary message,
    /// primaryMessage is the PrimaryMessage log of the transaction and
    /// completedSource is the timer for the transaction
    /// </summary>
    public ConcurrentDictionary<uint, (string Name, TaskCompletionSource<ILoggedSecsGemMessage>
            completeSource)>
        ReplyExpectedMessages
    { get; } = [];

    private IHSMSParameters HsmsParameters { get; }
    private ILogger<TransactionHandler> Logger { get; }
    private CommunicationHandler CommunicationHandler { get; }
    private CancellationTokenSource Cancellation { get; } = new();

    /// <summary>
    /// Event that is raised when a reply <see cref="SecsGemDataMessage"/> is received from the remote device
    /// and properly matched with its primary message.
    /// </summary>
    public IEventBus<ILoggedTransaction> OnTransactionMatched { get; }

    public async Task<(TransactionHandlerError error, ILoggedDataMessage? reply)> SendTransaction(SecsGemDataMessage message)
    {
        var token = new TaskCompletionSource<ILoggedSecsGemMessage>();
        var systemBytes = CreateSystemBytes();

        if (message is { Reply: false })
        {
            return (TransactionHandlerError.DoesNotRequireAReply, null);
        }

        ReplyExpectedMessages[systemBytes] = (message.Name, token);

        var loggedMessage = await CommunicationHandler.SendAndLogMessage(message, systemBytes);

        try
        {
            if (loggedMessage.Status is not MessageStatus.Success)
                return (TransactionHandlerError.MessageNotSent, null);

            var reply = await token.Task.WaitAsync(TimeSpan.FromMilliseconds(HsmsParameters.T3), Cancellation.Token)
                .ConfigureAwait(false);

            if (reply is ILoggedDataMessage dataReply)
            {
                return (TransactionHandlerError.None, dataReply);
            }

            return (TransactionHandlerError.IncorrectMessageReplyType, null);
        }
        catch (TaskCanceledException)
        {
            Logger.LogWarning("Await reply for {message} ({systemBytes}) was cancelled", message.Name, systemBytes);
            return (TransactionHandlerError.Cancelled, null);
        }
        catch (TimeoutException)
        {
            Logger.LogError(
                "T3 Timeout for message [message ID: 0x{systemBytes:X8}] : Currently set at {timeout} ms.", systemBytes, HsmsParameters.T3);
            CommunicationHandler.RestartConnection();
            return (TransactionHandlerError.Timeout, null);
        }
        finally
        {
            ReplyExpectedMessages.TryRemove(systemBytes, out _);
        }
    }

    public void TryHandleDataMessageReply(ILoggedDataMessage reply)
    {
        if (!ReplyExpectedMessages.TryGetValue(reply.HeaderData.SystemBytes, out var transaction))
        {
            if (DataMessageIsReply(reply.Data))
            {
                //TODO: handle send reject
            }

            return;
        }

        transaction.completeSource.SetResult(reply);
    }

    public async Task<(TransactionHandlerError error, ILoggedControlMessage? reply)> SendControlTransaction(HeaderData transaction)
    {
        var token = new TaskCompletionSource<ILoggedSecsGemMessage>();
        transaction.SystemBytes = CreateSystemBytes();

        if (transaction.SessionType == (byte)SessionType.DataMessage)
        {
            Logger.LogError("{Method name} received a message that was of type Data Message", nameof(SendControlTransaction));
            return (TransactionHandlerError.PassedIncorrectSessionType, null);
        }

        if (!ControlMessageRequiresReply(transaction))
            return (TransactionHandlerError.DoesNotRequireAReply, null);

        ReplyExpectedMessages[transaction.SystemBytes] = 
            (transaction.SessionType.ToString(), token);

        try
        {
            var loggedMessage = await CommunicationHandler.SendStateMessage(transaction);

            if (loggedMessage.Status is not MessageStatus.Success)
                return (TransactionHandlerError.MessageNotSent, null);

            var reply = await token.Task.WaitAsync(TimeSpan.FromMilliseconds(HsmsParameters.T6), Cancellation.Token)
                .ConfigureAwait(false);

            if (reply is ILoggedControlMessage controlReply)
            {
                return (TransactionHandlerError.None, controlReply);
            }

            return (TransactionHandlerError.IncorrectMessageReplyType, null);
        }
        catch (TimeoutException)
        {
            Logger.LogError(
                "T6 Timeout for message [message ID: 0x{SystemBytes:X8}] : Currently set at {Timeout} ms.", transaction.SystemBytes, HsmsParameters);
            CommunicationHandler.RestartConnection();

            return (TransactionHandlerError.Timeout, null);
        }
        finally
        {
            ReplyExpectedMessages.TryRemove(transaction.SystemBytes, out _);
        }
    }

    public void TryHandleControlMessageReply(ILoggedControlMessage reply)
    {
        if (!ReplyExpectedMessages.TryGetValue(reply.HeaderData.SystemBytes, out var transaction))
        {
            if (ControlMessageIsReply(reply.HeaderData))
            {
                //TODO: handle send reject
            }

            return;
        }

        transaction.completeSource.TrySetResult(reply);
    }

    public uint CreateSystemBytes()
    {
        uint systemBytes = 0;
        while (systemBytes == 0 || ReplyExpectedMessages.ContainsKey(systemBytes))
        {
            systemBytes = MessageIdGenerator.NewId();
        }

        return systemBytes;
    }

    private void RemoveAllTransactions(object? sender, EventArgs eventArgs)
    {
        foreach (var key in ReplyExpectedMessages.Keys)
        {
            if (ReplyExpectedMessages.TryRemove(key, out var transaction))
            {
                transaction.completeSource.TrySetCanceled();
            }
        }
    }

    private static bool ControlMessageRequiresReply(HeaderData header)
    {
        return (SessionType)header.SessionType is SessionType.SelectReq or SessionType.DeselectReq or SessionType.LinktestReq;
    }

    private static bool DataMessageIsReply(SecsGemDataMessage message)
    {
        return message is not { Stream: 9 } || message.Function % 2 == 0;
    }

    private static bool ControlMessageIsReply(HeaderData header)
    {
        return (SessionType)header.SessionType is SessionType.SelectRsp or SessionType.DeselectRsp or SessionType.LinktestRsp or SessionType.RejectReq;
    }
}
