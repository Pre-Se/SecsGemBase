using CommunityToolkit.Mvvm.ComponentModel;

namespace SecsGemBaseItems.SecsGemParameters;

/// <summary>
/// Contains the parameters for the HSMS protocol
/// </summary>
public partial class HSMSParameters : ObservableObject, IHSMSParameters
{
    public const string Section = nameof(HSMSParameters);
    public uint T3 { get; set; } = 45000;
    [ObservableProperty]
    public partial uint T5 { get; set; } = 10000;
    public uint T6 { get; set; } = 5000;
    public uint T7 { get; set; } = 10000;
    public uint T8 { get; set; } = 10000;
    public bool LinktestSend { get; set; }
    public uint LinktestInterval { get; set; }
    public bool InitiateSelectRequest { get; set; }
    public bool IgnoreState { get; set; } = false;
    public ushort SessionId { get; set; } = 0;
    public bool CommunicationsEnabled { get; set; } = true;
    public uint CommunicationsWaitDelay { get; set; } = 1000;
    public bool ControlMessageSessionIdCompatibility { get; set; }
    public void CopyFrom(IHSMSParameters source)
    {
        T3 = source.T3;
        T5 = source.T5;
        T6 = source.T6;
        T7 = source.T7;
        T8 = source.T8;
        LinktestSend = source.LinktestSend;
        LinktestInterval = source.LinktestInterval;
        InitiateSelectRequest = source.InitiateSelectRequest;
        IgnoreState = source.IgnoreState;
        CommunicationsEnabled = source.CommunicationsEnabled;
        CommunicationsWaitDelay = source.CommunicationsWaitDelay;
        SessionId = source.SessionId;
        ControlMessageSessionIdCompatibility = source.ControlMessageSessionIdCompatibility;
    }

    public IHSMSParameters GetCopySource()
    {
        return this;
    }
}