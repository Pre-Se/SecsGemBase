namespace SecsGemMessageHandling.Events.Enums;

/// <summary>
/// Define Report Acknowledge Code used for S2F34
/// </summary>
public enum DRACK
{
    Accepted = 0,
    InsufficientSpace = 1,
    InvalidFormat = 2,
    RptidAlreadyDefined = 3,
    VidNotDefined = 4
}
