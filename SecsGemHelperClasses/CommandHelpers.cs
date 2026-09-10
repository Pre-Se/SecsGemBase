using System.Windows.Input;

namespace SecsGemHelperClasses;

public static class CommandHelpers
{
    public static bool RunCommand(ICommand command, object? parameter)
    {
        if (!command.CanExecute(parameter))
            return false;
        command.Execute(parameter);
        return true;
    }
}