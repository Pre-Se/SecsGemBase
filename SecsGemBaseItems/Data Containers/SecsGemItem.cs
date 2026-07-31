using CommunityToolkit.Mvvm.ComponentModel;
using SecsGemBaseItems.Data_Containers.Interfaces;
using SecsGemBaseItems.Enums;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemHelperClasses;
using SecsGemHelperClasses.Copy;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace SecsGemBaseItems.Data_Containers;

public abstract partial class SecsGemItem : DataItem, ITurnToBytes, ICopy<SecsGemItem>, ICanBeParent, IDeepCloneable<SecsGemItem>
{
    [ObservableProperty]
    public partial SecsGemItemFormatType FormatType { get; set; }

    protected SecsGemItem()
    {
        PropertyChanged += OnPropertyChanged;
        Children.CollectionChanged += OnChildrenChanged;
    }

    public abstract int GetSize();
    public abstract IEnumerable<object> GetBoxedValues();
    public abstract ReadOnlyMemory<byte> ToBytes();
    public abstract SecsGemItem Clone();
    public abstract void CopyFrom(SecsGemItem source);
    public abstract void SetValuesFromStrings(IEnumerable<string> strings);
    public abstract IEnumerable<string> GetStringValues();

    public SecsGemItem GetCopySource() => this;

    protected int GetSizeBasicTypes()
    {
        return SecsGemItemFormat.FormatDictionary.GetValueOrDefault(FormatType);
    }

    protected abstract void SetName();

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FormatType) or nameof(Children))
            SetName();
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SetName();
    }

    protected static byte[] GetLengthBytes(int sizeOfItem)
    {
        var lengthBytes = MessageHandlingHelpers.Reverse(BitConverter.GetBytes(sizeOfItem), sizeof(int));
        var lengthList = lengthBytes.ToList();

        switch (sizeOfItem)
        {
            case > 0xFFFF and <= 0xFFFFFF:
                lengthList.RemoveRange(0, 1); break;
            case > 0xFF and <= 0xFFFF:
                lengthList.RemoveRange(0, 2); break;
            case >= 0 and <= 0xFF:
                lengthList.RemoveRange(0, 3); break;
        }
        lengthBytes = [.. lengthList];
        return (lengthBytes.Length is > 0 and < 4) ? lengthBytes : [];
    }

    protected byte[] CreateItemHeaderBytes()
    {
        var sizeOfItem = GetSize();
        var lengthBytes = GetLengthBytes(sizeOfItem);
        var formatByte = (byte)((int)FormatType + lengthBytes.Length);
        return MessageHandlingHelpers.Combine([formatByte], lengthBytes);
    }

    public bool CheckValue(SecsGemItemFormatType type, string[]? valueStrings)
    {
        if (type != FormatType) return false;
        if (valueStrings is null) return true;

        var strings = GetStringValues().ToList();
        if (strings.Count != valueStrings.Length) return false;
        return !strings.Where((t, i) => !string.Equals(t, valueStrings[i], StringComparison.Ordinal)).Any();
    }

    public SecsGemItem? this[int index] => Children.ElementAtOrDefault(index) as SecsGemItem;

    public bool CanAddChild(IDataItem? child) => child is SecsGemItem && FormatType == SecsGemItemFormatType.List;

    public bool TryAddChild(IDataItem child)
    {
        if (!CanAddChild(child)) return false;
        Children.Add(child);
        return true;
    }

    public bool TryRemoveChild(IDataItem child) => Children.Remove(child);

    public bool TryGetValue<T>([NotNullWhen(true)] out T? value)
    {
        value = default;
        if (!TypeMatchesEnum(typeof(T), FormatType))
            return false;
        try
        {
            var boxed = GetBoxedValues().FirstOrDefault();
            if (boxed is null) return false;
            value = (T)Convert.ChangeType(boxed, typeof(T));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TypeMatchesEnum(Type t, SecsGemItemFormatType key)
    {
        return key switch
        {
            SecsGemItemFormatType.Boolean => t == typeof(bool),
            SecsGemItemFormatType.Binary => t == typeof(byte),
            SecsGemItemFormatType.ASCII => t == typeof(string),
            SecsGemItemFormatType.U1 => t == typeof(byte),
            SecsGemItemFormatType.U2 => t == typeof(ushort),
            SecsGemItemFormatType.U4 => t == typeof(uint),
            SecsGemItemFormatType.U8 => t == typeof(ulong),
            SecsGemItemFormatType.I1 => t == typeof(sbyte),
            SecsGemItemFormatType.I2 => t == typeof(short),
            SecsGemItemFormatType.I4 => t == typeof(int),
            SecsGemItemFormatType.I8 => t == typeof(long),
            SecsGemItemFormatType.Float => t == typeof(float),
            SecsGemItemFormatType.Double => t == typeof(double),
            SecsGemItemFormatType.JIS8 or SecsGemItemFormatType.TwoByteCharacter or SecsGemItemFormatType.List => false,
            _ => false
        };
    }

    public static SecsGemItem Create(SecsGemItemFormatType formatType)
    {
        if (formatType == SecsGemItemFormatType.List)
            return new SecsGemListItem();

        return formatType switch
        {
            SecsGemItemFormatType.Binary => new SecsGemValueItem<byte> { FormatType = formatType },
            SecsGemItemFormatType.U1 => new SecsGemValueItem<byte> { FormatType = formatType },
            SecsGemItemFormatType.I1 => new SecsGemValueItem<sbyte> { FormatType = formatType },
            SecsGemItemFormatType.Boolean => new SecsGemValueItem<bool> { FormatType = formatType },
            SecsGemItemFormatType.U2 => new SecsGemValueItem<ushort> { FormatType = formatType },
            SecsGemItemFormatType.I2 => new SecsGemValueItem<short> { FormatType = formatType },
            SecsGemItemFormatType.U4 => new SecsGemValueItem<uint> { FormatType = formatType },
            SecsGemItemFormatType.I4 => new SecsGemValueItem<int> { FormatType = formatType },
            SecsGemItemFormatType.U8 => new SecsGemValueItem<ulong> { FormatType = formatType },
            SecsGemItemFormatType.I8 => new SecsGemValueItem<long> { FormatType = formatType },
            SecsGemItemFormatType.Float => new SecsGemValueItem<float> { FormatType = formatType },
            SecsGemItemFormatType.Double => new SecsGemValueItem<double> { FormatType = formatType },
            SecsGemItemFormatType.ASCII or SecsGemItemFormatType.JIS8 or SecsGemItemFormatType.TwoByteCharacter
                => new SecsGemValueItem<string> { FormatType = formatType },
            _ => throw new ArgumentOutOfRangeException(nameof(formatType))
        };
    }

    public static bool IsEquivalent(SecsGemItem? a, SecsGemItem? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.FormatType != b.FormatType) return false;

        var aVals = a.GetBoxedValues().ToList();
        var bVals = b.GetBoxedValues().ToList();
        if (aVals.Count != bVals.Count) return false;
        for (int i = 0; i < aVals.Count; i++)
        {
            if (!Equals(aVals[i], bVals[i]))
                return false;
        }

        if (a.Children.Count != b.Children.Count) return false;
        for (int i = 0; i < a.Children.Count; i++)
        {
            if (a.Children[i] is not SecsGemItem childA || b.Children[i] is not SecsGemItem childB)
                return false;
            if (!IsEquivalent(childA, childB))
                return false;
        }

        return true;
    }
}
