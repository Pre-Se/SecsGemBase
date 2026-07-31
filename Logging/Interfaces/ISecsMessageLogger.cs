using System.Collections.ObjectModel;

namespace Logging.Interfaces;

public interface ISecsMessageLogger
{
    /// <summary>
    /// Collection of sent and received SECS/GEM messages
    /// </summary>
    public ObservableCollection<ILoggedSecsGemMessage> MessagesLog { get; }

    /// <summary>
    /// Logs a received SECS/GEM message
    /// </summary>
    /// <param name="loggedDataMessage"></param>
    public void MessageIn(ILoggedDataMessage loggedDataMessage);

    /// <summary>
    /// Logs a sent SECS/GEM message
    /// </summary>
    /// <param name="loggedDataMessage"></param>
    public void MessageOut(ILoggedDataMessage loggedDataMessage);

    /// <summary>
    /// Logs a received SECS/GEM control message
    /// </summary>
    /// <param name="loggedControlMessage"></param>
    public void ControlMessageIn(ILoggedControlMessage loggedControlMessage);

    /// <summary>
    /// Logs a sent SECS/GEM message
    /// </summary>
    /// <param name="loggedControlMessage"></param>
    public void ControlMessageOut(ILoggedControlMessage loggedControlMessage);
}