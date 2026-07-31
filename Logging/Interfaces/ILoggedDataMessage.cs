using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Header;

namespace Logging.Interfaces;
public interface ILoggedDataMessage : ILoggedSecsGemMessage
{
    /// <summary>
    /// Metadata of the message
    /// </summary>
    HeaderData HeaderData { get; init; }

    /// <summary>
    /// In case the message is a data message, this will contain the data of the message
    /// </summary>
    SecsGemDataMessage Data { get; init; }
}
