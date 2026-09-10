using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Header;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemBaseItems.SecsGemParameters.Enums;
using SecsGemMessageHandling.Enums;

namespace SecsGemMessageHandling.Helpers;
public class ControlMessageFactory(IHSMSParameters hsmsParameters)
{
    private IHSMSParameters HSMSParameters { get; } = hsmsParameters;

    public HeaderData CreateDataMessageHeader(SecsGemDataMessage message, uint systemBytes)
    {
        return new HeaderData
        {
            HeaderByte2 = GetHeaderByte2FromMessage(message),
            HeaderByte3 = message.Function,
            DeviceId = HSMSParameters.SessionId,
            SessionType = (byte)SessionType.DataMessage,
            SystemBytes = systemBytes
        };
    }

    public HeaderData CreateSelectRequest()
    {
        return new HeaderData
        {
            DeviceId = HSMSParameters.ControlMessageSessionIdCompatibility ? (ushort)0xFFFF : HSMSParameters.SessionId,
            SessionType = (byte)SessionType.SelectReq
        };
    }

    public static HeaderData CreateSelectResponse(uint systemBytes, ushort sessionId, SelectStatus status)
    {
        return new HeaderData
        {
            DeviceId = sessionId,
            HeaderByte3 = (byte)status,
            SessionType = (byte)SessionType.SelectRsp,
            SystemBytes = systemBytes
        };
    }

    public HeaderData CreateDeselectRequest()
    {
        return new HeaderData
        {
            DeviceId = HSMSParameters.ControlMessageSessionIdCompatibility ? (ushort)0xFFFF : HSMSParameters.SessionId,
            SessionType = (byte)SessionType.DeselectReq
        };
    }

    public static HeaderData CreateDeselectResponse(uint systemBytes, ushort sessionId, DeselectStatus status)
    {
        return new HeaderData
        {
            DeviceId = sessionId,
            HeaderByte3 = (byte)status,
            SessionType = (byte)SessionType.DeselectRsp,
            SystemBytes = systemBytes
        };
    }

    public static HeaderData CreateLinktestRequest()
    {
        return new HeaderData
        {
            DeviceId = 0xFFFF,
            SessionType = (byte)SessionType.LinktestReq
        };
    }

    public static HeaderData CreateLinktestResponse(uint systemBytes)
    {
        return new HeaderData
        {
            DeviceId = 0xFFFF,
            SessionType = (byte)SessionType.LinktestRsp,
            SystemBytes = systemBytes
        };
    }

    public static HeaderData CreateRejectRequest(uint systemBytes, ushort sessionId, RejectReason reason, byte rejectedType = 0)
    {
        return new HeaderData
        {
            DeviceId = sessionId,
            HeaderByte2 = rejectedType,
            HeaderByte3 = (byte)reason,
            SessionType = (byte)SessionType.RejectReq,
            SystemBytes = systemBytes
        };
    }

    public HeaderData CreateSeparateRequest(uint systemBytes)
    {
        return new HeaderData
        {
            DeviceId = HSMSParameters.SessionId,
            SessionType = (byte)SessionType.SeparateReq,
            SystemBytes = systemBytes
        };
    }

    private static byte GetHeaderByte2FromMessage(SecsGemDataMessage message)
    {
        return (byte)(((message.Reply ? 1 : 0) << 7) + message.Stream);
    }
}
