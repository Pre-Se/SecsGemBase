using Logging.Interfaces;
using SecsGemBaseItems.Enums;
using System.Numerics;

namespace Logging;
public record LoggedControlStateDataMessage : ILoggedControlStateDataMessage, IEqualityOperators<LoggedControlStateDataMessage, LoggedControlStateDataMessage, bool>
{
    public required ILoggedDataMessage Message { get; init; }
    public required ControlState ControlState { get; init; }
    public required ControlSubstate ControlSubstate { get; init; }
}