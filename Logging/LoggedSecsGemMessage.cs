using System.Numerics;
using Logging.Enums;
using Logging.Interfaces;

namespace Logging;

public record LoggedSecsGemMessage(ReadOnlyMemory<byte> RawBytes, MessageResult Result) : ILoggedSecsGemMessage, IEqualityOperators<LoggedSecsGemMessage, LoggedSecsGemMessage, bool>
{
    public string Header => SetHeader();
    public ReadOnlyMemory<byte> RawData { get; } = RawBytes;
    public MessageResult Result { get; } = Result;
    public MessageStatus Status { get; init; } = MessageStatus.Pending;
    public DateTime TimeStamp { get; } = DateTime.Now;

    private string SetHeader()
    {
        var header = Result + "  Unknown message";

        return header;
    }
}