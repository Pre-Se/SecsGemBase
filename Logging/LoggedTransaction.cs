using Logging.Interfaces;

namespace Logging;
public class LoggedTransaction(ILoggedSecsGemMessage primaryMessage, ILoggedSecsGemMessage secondaryMessage)
    : ILoggedTransaction
{
    public ILoggedSecsGemMessage PrimaryMessage { get; } = primaryMessage;
    public ILoggedSecsGemMessage SecondaryMessage { get; } = secondaryMessage;
}
