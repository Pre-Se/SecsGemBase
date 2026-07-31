using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Interfaces;
using SecsGemBaseItems.Enums;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemHelperClasses;

namespace SecsGemMessageHandling.Data_Handling;

internal class MessageParsing(byte[] messageBytes)
{
    private int currentByte = 14;

    public void GetItems(SecsGemDataMessage parent)
    {
        while (currentByte < messageBytes.Length)
        {
            ReadItem(parent);
        }
    }
    private void ReadItem(ICanBeParent parent)
    {
        var formatType = GetItemDataType(messageBytes[currentByte]);
        var item = SecsGemItem.Create(formatType);

        item.SetParent(parent);

        var numberOfLengthBytes = GetItemLengthBytes(messageBytes[currentByte]);
        currentByte++;

        var itemLength = ReadItemLength(numberOfLengthBytes);
        currentByte += numberOfLengthBytes;

        if (itemLength == 0) return;

        ReadItemData(item, itemLength);
    }
    private int ReadItemLength(int numberOfLengthBytes)
    {
        var itemLengthBytes = MessageHandlingHelpers.Reverse(messageBytes, numberOfLengthBytes, currentByte);
        return BitConverter.ToInt32(MessageHandlingHelpers.AddPaddingBytes(itemLengthBytes, sizeof(int)), 0);
    }
    private static int GetItemLengthBytes(byte itemHeader1)
    {
        var numberOfLengthBytes = (int)(itemHeader1) & 0b00000011;
        if (numberOfLengthBytes != 0)
        {
            return numberOfLengthBytes;
        }

        throw new Exception("Invalid number of length bytes in item");
    }
    private static SecsGemItemFormatType GetItemDataType(byte itemHeader1)
    {
        var dataType = (SecsGemItemFormatType)((int)(itemHeader1) & 0b11111100);
        if (Enum.IsDefined(dataType))
        {
            return dataType;
        }

        throw new InvalidEnumArgumentException("Invalid item type received in message");
    }
    private void ReadItemData(SecsGemItem item, int itemLength)
    {
        if (item is SecsGemListItem)
        {
            for (var i = 0; i < itemLength; i++)
            {
                ReadItem(item);
            }
        }
        else if (item.FormatType == SecsGemItemFormatType.ASCII)
        {
            ReadAscii(item, itemLength);
        }
        else if (item.FormatType == SecsGemItemFormatType.TwoByteCharacter)
        {
        }
        else if (item.FormatType == SecsGemItemFormatType.JIS8)
        {
        }
        else if (SecsGemItemFormat.FormatDictionary.TryGetValue(item.FormatType, out var typeSize))
        {
            if (itemLength % typeSize != 0)
            {
                throw new Exception($"Invalid item length received; an item of type {item.FormatType} with length: {itemLength} was received and message couldn\'t be parsed");
            }

            var data = new byte[itemLength];
            Array.Copy(messageBytes, currentByte, data, 0, itemLength);
            currentByte += itemLength;

            if (item is SecsGemValueItem<byte> byteItem && typeSize == 1)
            {
                byteItem.ReadValuesFromBytes(data);
            }
            else if (item is SecsGemValueItem<bool> boolItem && typeSize == sizeof(bool))
            {
                boolItem.ReadValuesFromBytes(data);
            }
            else if (item is SecsGemValueItem<sbyte> sbyteItem && typeSize == sizeof(sbyte))
            {
                sbyteItem.ReadValuesFromBytes(data);
            }
            else if (item is SecsGemValueItem<ushort> ushortItem && typeSize == sizeof(ushort))
            {
                ushortItem.ReadValuesFromBytes(data);
            }
            else if (item is SecsGemValueItem<short> shortItem && typeSize == sizeof(short))
            {
                shortItem.ReadValuesFromBytes(data);
            }
            else if (item is SecsGemValueItem<uint> uintItem && typeSize == sizeof(uint))
            {
                uintItem.ReadValuesFromBytes(data);
            }
            else if (item is SecsGemValueItem<int> intItem && typeSize == sizeof(int))
            {
                intItem.ReadValuesFromBytes(data);
            }
            else if (item is SecsGemValueItem<ulong> ulongItem && typeSize == sizeof(ulong))
            {
                ulongItem.ReadValuesFromBytes(data);
            }
            else if (item is SecsGemValueItem<long> longItem && typeSize == sizeof(long))
            {
                longItem.ReadValuesFromBytes(data);
            }
            else if (item is SecsGemValueItem<float> floatItem && typeSize == sizeof(float))
            {
                floatItem.ReadValuesFromBytes(data);
            }
            else if (item is SecsGemValueItem<double> doubleItem && typeSize == sizeof(double))
            {
                doubleItem.ReadValuesFromBytes(data);
            }
        }
    }

    private void ReadAscii(SecsGemItem item, int itemLength)
    {
        byte[] asciiBytes = new byte[itemLength];
        Array.Copy(messageBytes, currentByte, asciiBytes, 0, itemLength);
        currentByte += itemLength;

        if (item is SecsGemValueItem<string> stringItem)
            stringItem.ReadValuesFromBytes(asciiBytes);
    }
}
