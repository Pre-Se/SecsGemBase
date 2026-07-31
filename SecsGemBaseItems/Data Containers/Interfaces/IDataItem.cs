using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SecsGemBaseItems.Data_Containers.Interfaces;

/// <summary>
/// Represents an item that can be displayed in a tree view as a string of text and can have children
/// </summary>
public interface IDataItem : INotifyPropertyChanged
{
    /// <summary>
    /// Name of the item
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Description of the item
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Header of the item, for display in the UI
    /// </summary>
    public string Header { get; }
    public void SetParent(ICanBeParent parent);
    /// <summary>
    /// Children items of this item
    /// </summary>
    public ObservableCollection<IDataItem> Children { get; }
}