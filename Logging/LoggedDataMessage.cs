using System.Numerics;
using Logging.Enums;
using Logging.Interfaces;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Header;

namespace Logging;
public record LoggedDataMessage : ILoggedDataMessage, IEqualityOperators<LoggedDataMessage, LoggedDataMessage, bool>
{
    public LoggedDataMessage(HeaderData parsedMessage,
        SecsGemDataMessage dataMessage, byte[] rawBytes, MessageResult result)
    {
        HeaderData = parsedMessage;
        Data = dataMessage;
        BaseMessage = new LoggedSecsGemMessage(rawBytes, result);
    }

    public LoggedDataMessage(ILoggedSecsGemMessage baseMessage, HeaderData parsedMessage, SecsGemDataMessage dataMessage)
    {
        HeaderData = parsedMessage;
        Data = dataMessage;
        BaseMessage = baseMessage;
    }

    public string Header => SetHeader();
    public ReadOnlyMemory<byte> RawData => BaseMessage.RawData;
    public DateTime TimeStamp => BaseMessage.TimeStamp;
    public MessageResult Result => BaseMessage.Result;
    public MessageStatus Status { get; init; }
    public HeaderData HeaderData { get; init; }
    public SecsGemDataMessage Data { get; init; }
    public ILoggedSecsGemMessage BaseMessage { get; }
    private string SetHeader()
    {
        var header = Result + " ";
        header += Data.Header;

        return header;
    }
}
