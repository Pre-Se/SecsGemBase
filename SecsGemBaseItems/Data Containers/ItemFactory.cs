using System.Globalization;
using SecsGemBaseItems.Enums;

namespace SecsGemBaseItems.Data_Containers;
public class ItemFactory
{
    private readonly List<SecsGemItem> items = [];

    public ItemFactory AddList(Action<ItemFactory> nested, string? description = null)
    {
        var childFactory = new ItemFactory();
        nested(childFactory);

        var childList = new SecsGemListItem { Description = description ?? string.Empty };
        foreach (var child in childFactory.Build())
            child.SetParent(childList);
        AddItem(childList);
        return this;
    }

    public ItemFactory AddBinary(byte value, string? description = null)
    {
        var child = new SecsGemValueItem<byte> { FormatType = SecsGemItemFormatType.Binary, Values = [value], Description = description ?? string.Empty };
        AddItem(child);
        return this;
    }

    public ItemFactory AddBoolean(bool value, string? description = null)
    {
        var child = new SecsGemValueItem<bool> { FormatType = SecsGemItemFormatType.Boolean, Values = [value], Description = description ?? string.Empty };
        AddItem(child);
        return this;
    }

    public ItemFactory AddAscii(string value, string? description = null)
    {
        var child = new SecsGemValueItem<string> { FormatType = SecsGemItemFormatType.ASCII, Values = [value], Description = description ?? string.Empty };
        AddItem(child);
        return this;
    }

    public ItemFactory AddJis8(string value, string? description = null)
    {
        var item = new SecsGemValueItem<string> { FormatType = SecsGemItemFormatType.JIS8, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public ItemFactory AddTwoByteCharacter(string value, string? description = null)
    {
        var item = new SecsGemValueItem<string> { FormatType = SecsGemItemFormatType.TwoByteCharacter, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public ItemFactory AddU1(byte value, string? description = null)
    {
        var item = new SecsGemValueItem<byte> { FormatType = SecsGemItemFormatType.U1, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public ItemFactory AddU2(ushort value, string? description = null)
    {
        var item = new SecsGemValueItem<ushort> { FormatType = SecsGemItemFormatType.U2, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public ItemFactory AddU4(uint value, string? description = null)
    {
        var item = new SecsGemValueItem<uint> { FormatType = SecsGemItemFormatType.U4, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public ItemFactory AddU8(ulong value, string? description = null)
    {
        var item = new SecsGemValueItem<ulong> { FormatType = SecsGemItemFormatType.U8, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public ItemFactory AddI1(sbyte value, string? description = null)
    {
        var item = new SecsGemValueItem<sbyte> { FormatType = SecsGemItemFormatType.I1, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public ItemFactory AddI2(short value, string? description = null)
    {
        var item = new SecsGemValueItem<short> { FormatType = SecsGemItemFormatType.I2, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public ItemFactory AddI4(int value, string? description = null)
    {
        var item = new SecsGemValueItem<int> { FormatType = SecsGemItemFormatType.I4, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public ItemFactory AddI8(long value, string? description = null)
    {
        var item = new SecsGemValueItem<long> { FormatType = SecsGemItemFormatType.I8, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public ItemFactory AddFloat(float value, string? description = null)
    {
        var item = new SecsGemValueItem<float> { FormatType = SecsGemItemFormatType.Float, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public ItemFactory AddDouble(double value, string? description = null)
    {
        var item = new SecsGemValueItem<double> { FormatType = SecsGemItemFormatType.Double, Values = [value], Description = description ?? string.Empty };
        AddItem(item);
        return this;
    }

    public IList<SecsGemItem> Build() => items;

    public void AddItem(SecsGemItem item)
    {
        items.Add(item);
    }
}
