namespace SecsGemBaseItems.Enums;

public enum SecsGemItemFormatType
{
    List = 0x00, //00
    Binary = 0x20, //10
    Boolean = 0x24, //11
    ASCII = 0x40, //20
    JIS8 = 0x44, //21
    TwoByteCharacter = 0x48, //22
    U1 = 0xA4, //51
    U2 = 0xA8, //52
    U4 = 0xB0, //54
    U8 = 0xA0, //50
    I1 = 0x64, //31 
    I2 = 0x68, //32
    I4 = 0x70, //34
    I8 = 0x60, //30
    Float = 0x90, //44
    Double = 0x80 //40
}