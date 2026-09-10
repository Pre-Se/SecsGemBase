using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SecsGemBaseItems.Data_Containers.Header;
using SecsGemBaseItems.Data_Containers.Interfaces;
using SecsGemHelperClasses;

namespace SecsGemBaseItems.Data_Containers;

/// <summary>
/// Represents a SECS/GEM message, which it's <see cref="HeaderData.SessionType"/> is 0
/// </summary>
public partial class SecsGemDataMessage : DataItem, ISecsGemDataMessage
{
    /// <summary>
    /// Flag that tells if the sender of the message expects a reply
    /// </summary>
    [ObservableProperty]
    public partial bool Reply { get; set; }

    /// <summary>
    /// Represents the stream of the message, values from 0 to 127
    /// </summary>
    [ObservableProperty]
    public partial byte Stream { get; set; }

    /// <summary>
    /// Represents the function of the message
    /// </summary>
    [ObservableProperty]
    public partial byte Function { get; set; }

    [ObservableProperty]
    public partial bool IsPrimary { get; set; }

    public SecsGemDataMessage()
    {
        SetName();
        PropertyChanged += SetName;
    }

    private void SetName()
    {
        Name = $"S{Stream}F{Function}";
    }
    private void SetName(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Reply) or nameof(Stream) or nameof(Function))
            SetName();
    }

    public ReadOnlyMemory<byte> ToBytes()
    {
        var byteItems = Array.Empty<byte>();

        byteItems = Children.OfType<SecsGemItem>().Aggregate(byteItems, (current, item) => MessageHandlingHelpers.Combine(current, item.ToBytes().ToArray()));

        return byteItems;
    }

    /// <summary>
    /// Builds this <see cref="SecsGemDataMessage"/> using the <see cref="HeaderData"/>
    /// </summary>
    /// <param name="headerData"></param>
    /// <exception cref="NotImplementedException"></exception>
    public void GetDataFromHeader(HeaderData headerData)
    {
        if(headerData is { PresentationType: 0, SessionType: 0 })
        {
            Reply = (headerData.HeaderByte2 & 0x80) == 0x80;
            Stream = (byte)(headerData.HeaderByte2 & ~0x80);
            Function = headerData.HeaderByte3;
        }
        else
        {
            throw new NotImplementedException();
        }
    }
    public void CopyFrom(SecsGemDataMessage source)
    {
        Reply = source.Reply;
        Stream = source.Stream;
        Function = source.Function;
        IsPrimary = source.IsPrimary;
        Description = source.Description;
    }

    public SecsGemDataMessage GetCopySource()
    {
        return this;
    }
    public SecsGemItem? this[int index] => Children.ElementAtOrDefault(index) as SecsGemItem;

    public bool CanAddChild(IDataItem? child) => child is SecsGemItem;

    public bool TryAddChild(IDataItem child)
    {
        if (!CanAddChild(child)) return false;
        Children.Add(child);
        return true;
    }

    public bool TryRemoveChild(IDataItem child) => Children.Remove(child);

    public ISecsGemDataMessage Clone()
    {
        var clone = new SecsGemDataMessage();
        clone.CopyFrom(this);
        foreach (var child in Children.OfType<SecsGemItem>().Select(c => c.Clone()))
            child.SetParent(clone);
        return clone;
    }

    public static bool IsEquivalent(SecsGemDataMessage? a, SecsGemDataMessage? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        if (a.Stream != b.Stream) return false;
        if (a.Function != b.Function) return false;

        if (a.Children.Count != b.Children.Count) return false;
        for (int i = 0; i < a.Children.Count; i++)
        {
            if (a.Children[i] is not SecsGemItem childA || b.Children[i] is not SecsGemItem childB)
                return false;
            if (!SecsGemItem.IsEquivalent(childA, childB))
                return false;
        }

        return true;
    }
}
