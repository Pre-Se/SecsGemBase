using SecsGemBaseItems.Data_Containers.Header;

namespace Logging.Interfaces;
public interface ILoggedControlMessage : ILoggedSecsGemMessage
{
    /// <summary>
    /// Metadata of the message
    /// </summary>
    HeaderData HeaderData { get; }
}
