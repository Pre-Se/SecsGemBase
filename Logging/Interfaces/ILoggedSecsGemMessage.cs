using Logging.Enums;

namespace Logging.Interfaces;
public interface ILoggedSecsGemMessage
{
    /// <summary>
    /// Short description of the message that was logged to show in the UI
    /// </summary>
    string Header { get; }

    /// <summary>
    /// Message in pure byte form
    /// </summary>
    ReadOnlyMemory<byte> RawData { get; }

    /// <summary>
    /// Timestamp of the message, created after it was sent or before it was parsed when received
    /// </summary>
    DateTime TimeStamp { get; }

    /// <summary>
    /// Flags if the message was sent or received
    /// </summary>
    MessageResult Result { get; }

    /// <summary>
    /// Flags if the message was successfully or not sent
    /// </summary>
    MessageStatus Status { get; }
}
