using SecsGemBaseItems.Data_Containers.Interfaces;
using SecsGemHelperClasses;

namespace SecsGemBaseItems.Data_Containers.Header;

/// <summary>
/// Represents the header/metadata of a SECS/GEM message
/// </summary>
public class HeaderData : ITurnToBytes
{
    /// <summary>
    /// Message length in bytes of the sent message, excluding the 4 bytes of the <see cref="MessageLength"/> uinteger itself
    /// </summary>
    public uint MessageLength { get; private set; } = HeaderLength;

    /// <summary>
    /// Unique identifier of the device to which it is connected, both sent and received messages have to be the same
    /// </summary>
    public ushort DeviceId { get; set; }

    /// <summary>
    /// Multiple uses, represents the <see cref="SecsGemDataMessage.Reply"/> in the first bit and <see cref="SecsGemDataMessage.Stream"/> in the case of a <see cref="SecsGemDataMessage"/>
    /// </summary>
    public byte HeaderByte2 { get; set; }

    /// <summary>
    /// Multiple uses, represents the <see cref="SecsGemDataMessage.Function"/> in the case of a <see cref="SecsGemDataMessage"/>
    /// </summary>
    public byte HeaderByte3 { get; set; }

    /// <summary>
    /// Should always be 0, unless there is a custom extension of the standard
    /// </summary>
    public byte PresentationType { get; set; }

    /// <summary>
    /// Represents the type of message, see <see cref="SecsGemParameters.SecsGemSessionType"/>
    /// </summary>
    public byte SessionType { get;  set; }

    /// <summary>
    /// System bytes, used for message identification, should be unique for each <see cref="SecsGemTransaction"/>
    /// </summary>
    public uint SystemBytes { get; set; }
    private const uint HeaderLength = 10;
    /// <summary>
    /// Sets the length of the associated <see cref="SecsGemDataMessage"/> in bytes
    /// </summary>
    /// <param name="messageLength"></param>
    public void SetDataMessageLength(uint messageLength)
    {
        MessageLength = messageLength + HeaderLength;
    }
    public ReadOnlyMemory<byte> ToBytes()
    {
        var header = new byte[14];

        EncodeMessageLength(header, MessageLength);
        EncodeDeviceId(header, DeviceId);
        EncodeHeaderByte2(header, HeaderByte2);
        EncodeHeaderByte3(header, HeaderByte3);
        EncodePType(header, PresentationType);
        EncodeSType(header, SessionType);
        EncodeMessageId(header, SystemBytes);
        return header;
    }
    private static void EncodeMessageLength(byte[] header, uint messageLength)
    {
        MessageHandlingHelpers.InsertReversedDataIntoArray(header, BitConverter.GetBytes(messageLength), 0);
    }
    private static void EncodeDeviceId(byte[] header, ushort deviceId)
    {
        MessageHandlingHelpers.InsertReversedDataIntoArray(header, BitConverter.GetBytes(deviceId), 4);
    }
    private static void EncodeHeaderByte2(byte[] header, byte headerByte2)
    {
        header[6] = headerByte2;
    }
    private static void EncodeHeaderByte3(byte[] header, byte headerByte3)
    {
        header[7] = headerByte3;
    }
    private static void EncodePType(byte[] header, byte pType)
    {
        header[8] = pType;
    }
    private static void EncodeSType(byte[] header, byte sType)
    {
        header[9] = sType;
    }
    private static void EncodeMessageId(byte[] header, uint messageId)
    {
        MessageHandlingHelpers.InsertReversedDataIntoArray(header, BitConverter.GetBytes(messageId), 10);
    }

    public static bool DecodeHeader(ReadOnlyMemory<byte> message, out HeaderData header)
    {
        header = new HeaderData();
        if (message.Length < 14)
            return false;
        if (!header.CheckMessageLength(message))
            return false;
        if (!header.DecodeDeviceId(message))
            return false;
        if (!header.DecodeHeaderByte2(message))
            return false;
        if (!header.DecodeHeaderByte3(message))
            return false;
        if (!header.DecodePType(message))
            return false;
        if (!header.DecodeSType(message))
            return false;
        if (!header.DecodeSystemBytes(message))
            return false;

        return true;
    }

    private bool CheckMessageLength(ReadOnlyMemory<byte> message)
    {
        MessageLength = BitConverter.ToUInt32(MessageHandlingHelpers.Reverse(message.ToArray(), 4));

        return (MessageLength >= 10);
    }

    private bool DecodeDeviceId(ReadOnlyMemory<byte> message)
    {
        DeviceId = BitConverter.ToUInt16(MessageHandlingHelpers.Reverse(message.ToArray(), 2, 4));
        return true;
    }
    private bool DecodeHeaderByte2(ReadOnlyMemory<byte> message)
    {
        HeaderByte2 = message.Span[6];
        return true;
    }
    private bool DecodeHeaderByte3(ReadOnlyMemory<byte> message)
    {
        HeaderByte3 = message.Span[7];
        return true;
    }
    private bool DecodePType(ReadOnlyMemory<byte> message)
    {
        PresentationType = message.Span[8];
        return true;
    }
    private bool DecodeSType(ReadOnlyMemory<byte> message)
    {
        SessionType = message.Span[9];
        return true;
    }
    private bool DecodeSystemBytes(ReadOnlyMemory<byte> message)
    {
        SystemBytes = BitConverter.ToUInt32(MessageHandlingHelpers.Reverse(message.ToArray(), 4, 10));
        return true;
    }
}