using SecsGemBaseItems.Enums;

namespace Logging.Interfaces;

public interface ILoggedControlStateDataMessage
{
    public ILoggedDataMessage Message { get; init; }
    public ControlState ControlState { get; init; }
    public ControlSubstate ControlSubstate { get; init; }
}