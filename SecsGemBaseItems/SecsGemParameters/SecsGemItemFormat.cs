

using SecsGemBaseItems.Enums;

namespace SecsGemBaseItems.SecsGemParameters;

public static class SecsGemItemFormat
{
    public static Dictionary<SecsGemItemFormatType, int> FormatDictionary { get; }

    static SecsGemItemFormat()
    {
        FormatDictionary = new Dictionary<SecsGemItemFormatType, int>
        {
            [SecsGemItemFormatType.Binary] = sizeof(byte),
            [SecsGemItemFormatType.Boolean] = sizeof(bool),
            [SecsGemItemFormatType.Double] = sizeof(double),
            [SecsGemItemFormatType.Float] = sizeof(float),
            [SecsGemItemFormatType.I1] = sizeof(sbyte),
            [SecsGemItemFormatType.I2] = sizeof(short),
            [SecsGemItemFormatType.I4] = sizeof(int),
            [SecsGemItemFormatType.I8] = sizeof(long),
            [SecsGemItemFormatType.U1] = sizeof(byte),
            [SecsGemItemFormatType.U2] = sizeof(ushort),
            [SecsGemItemFormatType.U4] = sizeof(uint),
            [SecsGemItemFormatType.U8] = sizeof(ulong)
        };
        FormatDictionary.AsReadOnly();
    }
}