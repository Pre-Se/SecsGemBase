using CommunityToolkit.Mvvm.ComponentModel;
using SecsGemBaseItems.Enums;

namespace SecsGemMessageHandling.Data_Handling;

public partial class ControlStateInfo() 
    : ObservableObject
{
    [ObservableProperty]
    public partial ControlSubstate ControlSubstate { get; internal set; } = ControlSubstate.EquipmentOffline;
    [ObservableProperty]
    public partial ControlState ControlState { get; internal set; } = ControlState.Offline;

    public ControlSubstate DefaultOfflineSubstate
    {
        get;
        set
        {
            if (value != ControlSubstate.EquipmentOffline && value != ControlSubstate.HostOffline &&
                value != ControlSubstate.AttemptOnline)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = ControlSubstate.EquipmentOffline;
    public ControlSubstate DefaultFailedOnlineAttempt
    {
        get;
        set
        {
            if (value != ControlSubstate.EquipmentOffline && value != ControlSubstate.HostOffline)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = ControlSubstate.EquipmentOffline;
    public ControlSubstate OnlineSubstate
    {
        get;
        set
        {
            if (value != ControlSubstate.OnlineLocal && value != ControlSubstate.OnlineRemote)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = ControlSubstate.OnlineLocal;

    private readonly SemaphoreSlim controlStateSemaphore = new(1, 1);

    internal async Task RunWithLockAsync(Func<Task> action)
    {
        await controlStateSemaphore.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            controlStateSemaphore.Release();
        }
    }
}