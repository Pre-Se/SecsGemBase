using CommunityToolkit.Mvvm.ComponentModel;
using SecsGemBaseItems.Enums;
using SecsGemHelperClasses;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;

namespace SecsGemBaseItems.Data_Containers;

public partial class SecsGemValueItem<T> : SecsGemItem
{
    [ObservableProperty]
    public partial ObservableCollection<T> Values { get; set; } = [];

    public SecsGemValueItem()
    {
        Values.CollectionChanged += OnValuesChanged;
        SetName();
    }

    public override int GetSize()
    {
        return FormatType switch
        {
            SecsGemItemFormatType.ASCII => Values.Count > 0 ? (Values[0]?.ToString()?.Length ?? 0) : 0,
            SecsGemItemFormatType.JIS8 => throw new NotImplementedException(),
            SecsGemItemFormatType.TwoByteCharacter => throw new NotImplementedException(),
            _ => GetSizeBasicTypes() * Values.Count
        };
    }

    public override IEnumerable<object> GetBoxedValues()
    {
        foreach (var v in Values)
            yield return v!;
    }

    public override void SetValuesFromStrings(IEnumerable<string> strings)
    {
        Values.Clear();
        foreach (var s in strings)
        {
            if (string.IsNullOrEmpty(s))
                continue;

            try
            {
                var parsed = FormatType switch
                {
                    SecsGemItemFormatType.Binary when typeof(T) == typeof(byte) => (T)(object)Convert.ToByte(s, 16),
                    SecsGemItemFormatType.U1 when typeof(T) == typeof(byte) => (T)(object)byte.Parse(s),
                    SecsGemItemFormatType.I1 when typeof(T) == typeof(sbyte) => (T)(object)sbyte.Parse(s),
                    SecsGemItemFormatType.Boolean when typeof(T) == typeof(bool) => (T)(object)bool.Parse(s),
                    SecsGemItemFormatType.U2 => (T)(object)ushort.Parse(s),
                    SecsGemItemFormatType.I2 => (T)(object)short.Parse(s),
                    SecsGemItemFormatType.U4 => (T)(object)uint.Parse(s),
                    SecsGemItemFormatType.I4 => (T)(object)int.Parse(s),
                    SecsGemItemFormatType.U8 => (T)(object)ulong.Parse(s),
                    SecsGemItemFormatType.I8 => (T)(object)long.Parse(s),
                    SecsGemItemFormatType.Float => (T)(object)float.Parse(s, CultureInfo.InvariantCulture),
                    SecsGemItemFormatType.Double => (T)(object)double.Parse(s, CultureInfo.InvariantCulture),
                SecsGemItemFormatType.ASCII or SecsGemItemFormatType.JIS8 or SecsGemItemFormatType.TwoByteCharacter
                    when typeof(T) == typeof(string) => (T)(object)s,
                _ => throw new InvalidOperationException($"Cannot parse {typeof(T).Name} from string \"{s}\" for format {FormatType}")
                };
                Values.Add(parsed);
            }
            catch (FormatException) { }
            catch (OverflowException) { }
        }
    }

    public override IEnumerable<string> GetStringValues()
    {
        foreach (var v in Values)
        {
            yield return FormatType switch
            {
                SecsGemItemFormatType.Binary when v is byte b => b.ToString("X2"),
                SecsGemItemFormatType.ASCII or SecsGemItemFormatType.JIS8 or SecsGemItemFormatType.TwoByteCharacter => v?.ToString() ?? "",
                _ => v?.ToString() ?? ""
            };
        }
    }

    public override ReadOnlyMemory<byte> ToBytes()
    {
        var itemHeader = CreateItemHeaderBytes();
        var itemValue = ConvertValueToBytes();
        return MessageHandlingHelpers.Combine(itemHeader, itemValue);
    }

    private byte[] ConvertValueToBytes()
    {
        if (Values.Count == 0) return [];

        if (FormatType == SecsGemItemFormatType.ASCII)
            return Encoding.ASCII.GetBytes(Values[0]?.ToString() ?? "");
        if (FormatType == SecsGemItemFormatType.JIS8)
            throw new NotImplementedException();
        if (FormatType == SecsGemItemFormatType.TwoByteCharacter)
            throw new NotImplementedException();

        var elementSize = GetSizeBasicTypes();
        var result = new byte[Values.Count * elementSize];

        var typedArray = new T[Values.Count];
        Values.CopyTo(typedArray, 0);
        Buffer.BlockCopy(typedArray, 0, result, 0, result.Length);

        if (elementSize > 1)
        {
            for (var i = 0; i < Values.Count; i++)
                Array.Reverse(result, i * elementSize, elementSize);
        }

        return result;
    }

    public void ReadValuesFromBytes(byte[] data)
    {
        if (FormatType == SecsGemItemFormatType.ASCII)
        {
            Values.Clear();
            Values.Add((T)(object)Encoding.ASCII.GetString(data));
            return;
        }
        if (FormatType is SecsGemItemFormatType.JIS8 or SecsGemItemFormatType.TwoByteCharacter)
            throw new NotImplementedException();

        var elementSize = GetSizeBasicTypes();
        if (elementSize == 0) return;

        var count = data.Length / elementSize;
        var typedArray = new T[count];
        Buffer.BlockCopy(data, 0, typedArray, 0, data.Length);

        if (elementSize > 1)
        {
            for (var i = 0; i < count; i++)
                Array.Reverse(data, i * elementSize, elementSize);
            Buffer.BlockCopy(data, 0, typedArray, 0, data.Length);
        }

        Values.Clear();
        foreach (var v in typedArray)
            Values.Add(v);
    }

    public override SecsGemItem Clone()
    {
        var clone = new SecsGemValueItem<T>
        {
            FormatType = FormatType,
            Description = Description
        };
        clone.Values.Clear();
        foreach (var v in Values)
            clone.Values.Add(v);
        foreach (var child in Children.OfType<SecsGemItem>().Select(c => c.Clone()))
            child.SetParent(clone);
        return clone;
    }

    public override void CopyFrom(SecsGemItem source)
    {
        Description = source.Description;
        FormatType = source.FormatType;
        if (source is SecsGemValueItem<T> typedSource)
        {
            Values.Clear();
            foreach (var v in typedSource.Values)
                Values.Add(v);
        }
        else
        {
            Values.Clear();
        }
    }

    protected sealed override void SetName()
    {
        if (FormatType == SecsGemItemFormatType.Binary)
        {
            if (Values.Count < 10)
            {
                var hex = string.Concat(Values.Select(v => v is byte b ? b.ToString("X2") : ""));
                Name = $"{FormatType} = 0x{hex}";
            }
            else
                Name = $"{FormatType} = {Values.Count} Bytes";
            return;
        }
        if (Values is [var first, ..])
            Name = $"{FormatType} = {first}";
        else
            Name = $"{FormatType} = empty";
    }

    private void OnValuesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SetName();
    }
}
