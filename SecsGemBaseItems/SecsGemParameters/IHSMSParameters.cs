using System.ComponentModel;
using SecsGemHelperClasses.Copy;
using SecsGemBaseItems.SecsGemParameters.Enums;

namespace SecsGemBaseItems.SecsGemParameters;

public interface IHSMSParameters : ICopy<IHSMSParameters>, INotifyPropertyChanged
{
    /// <summary>
    /// Reply timeout: Specifies maximum amount of time an entity expecting
    /// reply message shall wait for that reply. Default is 45000ms
    /// </summary>
    uint T3 { get; set; }

    /// <summary>
    /// Connection Separation Timeout: Specifies the amount of time which shall
    /// elapse between successive attempts to connect to a given remote entity.
    /// Default is 10000ms
    /// </summary>
    uint T5 { get; set; }

    /// <summary>
    /// Control Transaction Timeout. Specifies the time which a control
    /// transaction may remain open before it is considered a communications
    /// failure. Default is 5000ms
    /// </summary>
    uint T6 { get; set; }

    /// <summary>
    /// Not Selected Timeout: Time which a TCP/IP connection can remain in NOT SELECTED state
    /// (i.e., no HSMS activity) before it is considered a communications failure. Default is 10000ms
    /// </summary>
    uint T7 { get; set; }

    /// <summary>
    /// Network Inter-character Timeout: Maximum time between successive bytes of a single HSMS message
    /// before it is considered a communications failure. Default is 5000ms
    /// </summary>
    uint T8 { get; set; }

    /// <summary>
    /// Flag that indicates if a linktest request should be periodically sent to the connected entity
    /// </summary>
    bool LinktestSend { get; set; }

    /// <summary> 
    /// If the <see cref="LinktestSend"/> flag is true, then this indicates the time interval in
    /// milliseconds at which a linktest request control message will be sent to the connected entity
    /// </summary>
    uint LinktestInterval { get; set; }

    /// <summary>
    /// Flag that indicates if a <see cref="SessionType.SelectReq"/> Control Message should be sent when in not selected control state
    /// </summary>
    bool InitiateSelectRequest { get; set; }

    /// <summary>
    /// Specifies if the current state will be ignored when sending or receiving a message.
    /// </summary>
    public bool IgnoreState { get; set; }

    /// <summary>
    /// Session ID details are defined in the Subordinate Standard. The Session
    /// ID is sometimes also known as a Device ID. Default is 0
    /// </summary>
    ushort SessionId { get; set; }

    /// <summary>
    /// Indicates if SECS-II communications are enabled
    /// </summary>
    bool CommunicationsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the CommDelay timer, in milliseconds, to wait before retrying communications.
    /// </summary>
    /// <remarks>This property is typically used to configure the wait time between communication attempts  in
    /// scenarios where retries are necessary. Setting this value to zero disables the delay.</remarks>
    uint CommunicationsWaitDelay { get; set; }

    /// <summary>
    /// if true, then all control messages request will have 0xFFFF set as Session ID
    /// </summary>
    bool ControlMessageSessionIdCompatibility { get; set; }
}