using SecsGemBaseItems.Enums;
using SecsGemHelperClasses;

namespace SecsGemBaseItems.Data_Containers;

public class SecsGemListItem : SecsGemItem
{
    public SecsGemListItem()
    {
        FormatType = SecsGemItemFormatType.List;
        SetName();
    }

    public override int GetSize() => Children.Count;

    public override IEnumerable<object> GetBoxedValues() => [];

    public override void SetValuesFromStrings(IEnumerable<string> strings) { }
    public override IEnumerable<string> GetStringValues() => [];

    public override ReadOnlyMemory<byte> ToBytes()
    {
        var itemHeader = CreateItemHeaderBytes();
        var childBytes = Children.OfType<SecsGemItem>()
            .Aggregate(Array.Empty<byte>(), (current, child) =>
                MessageHandlingHelpers.Combine(current, child.ToBytes().ToArray()));
        return MessageHandlingHelpers.Combine(itemHeader, childBytes);
    }

    public override SecsGemItem Clone()
    {
        var clone = new SecsGemListItem
        {
            Description = Description
        };
        foreach (var child in Children.OfType<SecsGemItem>().Select(c => c.Clone()))
            child.SetParent(clone);
        return clone;
    }

    public override void CopyFrom(SecsGemItem source)
    {
        Description = source.Description;
        FormatType = source.FormatType;
    }

    protected sealed override void SetName()
    {
        Name = $"{FormatType}({Children.Count})";
    }
}
