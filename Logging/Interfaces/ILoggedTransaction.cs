namespace Logging.Interfaces;
public interface ILoggedTransaction
{
    ILoggedSecsGemMessage PrimaryMessage { get; }
    ILoggedSecsGemMessage SecondaryMessage { get; }
}
