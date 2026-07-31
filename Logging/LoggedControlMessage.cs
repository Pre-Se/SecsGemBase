using System.Numerics;
using Logging.Enums;
using Logging.Interfaces;
using SecsGemBaseItems.Data_Containers.Header;
using SecsGemBaseItems.SecsGemParameters;

namespace Logging;
public record LoggedControlMessage : ILoggedControlMessage, IEqualityOperators<LoggedControlMessage, LoggedControlMessage, bool>
{
    public LoggedControlMessage(HeaderData parsedMessage,
        ReadOnlyMemory<byte> rawBytes, MessageResult result)
    {
        HeaderData = parsedMessage;
        BaseMessage = new LoggedSecsGemMessage(rawBytes, result);
    }

    public LoggedControlMessage(ILoggedSecsGemMessage baseMessage, HeaderData parsedMessage)
    {
        HeaderData = parsedMessage;
        BaseMessage = baseMessage;
    }

    public string Header => SetHeader();
    public ReadOnlyMemory<byte> RawData => BaseMessage.RawData;
    public DateTime TimeStamp => BaseMessage.TimeStamp;
    public MessageResult Result => BaseMessage.Result;
    public MessageStatus Status { get; init; } = MessageStatus.Pending;
    public HeaderData HeaderData { get; }
    public ILoggedSecsGemMessage BaseMessage { get; }


    private string SetHeader()
    {
        var header = $"{TimeStamp:HH:mm:ss.fff} {Result}";
        SecsGemSessionType.TryGetSessionType(HeaderData.SessionType, out var controlMessage);
        header += controlMessage;

        return header;
    }
}
