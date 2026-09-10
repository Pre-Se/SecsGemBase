using System.Collections.ObjectModel;
using SecsGemBaseItems.Data_Containers.Interfaces;

namespace SecsGemBaseItems.Data_Containers;

public class MessageFactory(
    byte stream,
    byte function,
    bool reply,
    string? description = null,
    ItemFactory? itemFactory = null)
{
    private readonly SecsGemDataMessage message = new()
    {
        Stream = stream,
        Function = function,
        Reply = reply,
        Description = description ?? string.Empty,
    };

    public SecsGemDataMessage Build()
    {
        message.Children = itemFactory != null ? new ObservableCollection<IDataItem>(itemFactory.Build()) : [];

        return message;
    }
}
