using System.Globalization;
using Logging.Interfaces;
using Microsoft.Extensions.Logging;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Enums;
using SecsGemBaseItems.LibraryManager;

namespace SecsGemMessageHandling.Data_Handling;
public class SpecialCasesHandling
{
    private CommunicationHandler CommunicationHandler { get; }
    private ILogger<SpecialCasesHandling> Logger { get; }
    private ISecsGemLibraryManager SecsGemLibraryManager { get; }

    public SpecialCasesHandling(CommunicationHandler communicationHandler, ILogger<SpecialCasesHandling> logger, ISecsGemLibraryManager secsGemLibraryManager)
    {
        CommunicationHandler = communicationHandler;
        Logger = logger;
        SecsGemLibraryManager = secsGemLibraryManager;
        CommunicationHandler.OnDataMessageIn.Subscribe(OnMessageIn);
    }

    private void OnMessageIn(ILoggedDataMessage e)
    {
        _ = SearchMessageLibraryForResponse(e.Data, e.HeaderData.SystemBytes);
    }

    private async Task SearchMessageLibraryForResponse(SecsGemDataMessage receivedMessage, uint systemBytes)
    {
        var library = SecsGemLibraryManager.Library;

        foreach (var transaction in library)
        {
            if (transaction.Children.ElementAt(0) is not SecsGemDataMessage primaryMessage) continue;

            if (primaryMessage.Stream != receivedMessage.Stream ||
                primaryMessage.Function != receivedMessage.Function) continue;

            if (transaction.Children.ElementAt(1) is not SecsGemDataMessage reply) continue;
            await CommunicationHandler.SendAndLogMessage(reply, systemBytes);
            HandleSentInternalDataMessage(reply);
            break;
        }
    }

    /// <summary>
    /// Handles any received <see cref="SecsGemDataMessage"/> that requires special handling
    /// </summary>
    /// <param name="valueSentTransaction"></param>
    /// <param name="receivedMessage"></param>
    public void HandleReceivedInternalDataMessage(SecsGemTransaction valueSentTransaction, SecsGemDataMessage receivedMessage)
    {
        var messageDirection = "received";

        if (!valueSentTransaction.CheckReceivedReply(receivedMessage))
        {
            return;
        }

        switch (receivedMessage.Stream)
        {
            case 1:
                switch (receivedMessage.Function)
                {
                    case 16:
                        HandleS1F16(receivedMessage, messageDirection);
                        break;
                    case 18:
                        HandleS1F18(receivedMessage, messageDirection);
                        break;
                }
                break;
        }
    }

    /// <summary>
    /// Handles any sent <see cref="SecsGemDataMessage"/> that requires special handling
    /// </summary>
    public void HandleSentInternalDataMessage(SecsGemDataMessage sentMessage)
    {
        string messageDirection = "sent";

        switch (sentMessage.Stream)
        {
            case 1:
                switch (sentMessage.Function)
                {
                    case 16:
                        HandleS1F16(sentMessage, messageDirection);
                        break;
                    case 18:
                        HandleS1F18(sentMessage, messageDirection);
                        break;
                }
                break;
        }
    }

    private void HandleS1F16(SecsGemDataMessage receivedMessage, string messageDirection)
    {
        if (!CheckIfVariableExist(receivedMessage, out var itemValue)) return;

        int.TryParse(itemValue, NumberStyles.HexNumber, null, out var offlineAcknowledge);

        switch (offlineAcknowledge)
        {
            case 0:
                //TODO: handle online state
                //CommunicationHandler.OnlineState = false;
                Logger.LogInformation("OFLACK {messageDirection}: OFF-LINE Request accepted", messageDirection);
                break;
            default:
                Logger.LogInformation("OFLACK {messageDirection}: Unknown response ({offlineAcknowledge})", messageDirection, offlineAcknowledge);
                break;
        }
    }

    private void HandleS1F18(SecsGemDataMessage receivedMessage, string messageDirection)
    {
        if (!CheckIfVariableExist(receivedMessage, out var itemValue)) return;

        int.TryParse(itemValue, NumberStyles.HexNumber, null, out var onlineAcknowledge);

        switch (onlineAcknowledge)
        {
            case 0:
                //CommunicationHandler.OnlineState = true;
                Logger.LogInformation("ONLACK {messageDirection}: ON-LINE Accepted", messageDirection);
                break;
            case 1:
                Logger.LogInformation("ONLACK {messageDirection}: ON-LINE Not Allowed", messageDirection);
                break;
            case 2:
                Logger.LogInformation("ONLACK {messageDirection}: Equipment already ON-LINE", messageDirection);
                break;
            default:
                Logger.LogInformation("ONLACK {messageDirection}: Unknown response ({onlineAcknowledge})", messageDirection, onlineAcknowledge);
                break;
        }
    }

    private static bool CheckIfVariableExist(SecsGemDataMessage receivedMessage, out string value)
    {
        value = "";
        if (receivedMessage.Children.Count != 1) return false;

        if (receivedMessage.Children[0] is not SecsGemItem item) return false;

        if (item.FormatType != SecsGemItemFormatType.Binary) return false;

        var boxed = item.GetBoxedValues().ToList();
        if (boxed.Count != 1) return false;

        value = boxed[0] is byte b ? b.ToString("X2") : "";
        return true;
    }
}
