using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Interfaces;
using SecsGemBaseItems.Enums;
using SecsGemMessageHandling.Events.Enums;

namespace SecsGemMessageHandling.Events.Models;

/// <summary>
/// Struct that links variable with variable ID
/// </summary>
public class SecsGemEquipmentVariable : IDeepCloneable<SecsGemEquipmentVariable>, IKeyedItem
{
    public SecsGemEquipmentVariable()
    {
        Name = string.Empty;
        Description = string.Empty;
    }
    public required int VariableId { get; init; }

    public string Name
    {
        get => Item.Name;
        set => Item.Name = value;
    }

    public string Value
    {
        get => Item.GetStringValues().FirstOrDefault() ?? "";
        set => Item.SetValuesFromStrings([value]);
    }

    public string Description
    {
        get => Item.Description;
        set => Item.Description = value;
    }

    public SecsGemItemFormatType DataType
    {
        get => Item.FormatType;
        set => Item.FormatType = value;
    }

    public SecsGemVariableClass VariableClass { get; set; }
    public SecsGemItem Item { get; set; } = new SecsGemValueItem<string>
    {
        FormatType = SecsGemItemFormatType.ASCII,
        Values = ["0"]
    };

    public SecsGemEquipmentVariable Clone()
    {
        if (MemberwiseClone() is not SecsGemEquipmentVariable clone)
            throw new InvalidOperationException(
                $"Clone failed: MemberwiseClone did not return a {GetType()}.");

        clone.Item = Item.Clone();

        return clone;
    }
    public int Id => VariableId;
    public static string LogPrefix => "Variable with ID";
}
