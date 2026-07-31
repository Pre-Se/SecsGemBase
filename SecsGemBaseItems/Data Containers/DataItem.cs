using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SecsGemBaseItems.Data_Containers.Interfaces;

namespace SecsGemBaseItems.Data_Containers;
/// <summary>
/// Base class for visualizing data in a tree view
/// </summary>
public partial class DataItem: ObservableObject, IDataItem
{
    /// <summary>
    /// Represents the name that will be displayed in the tree view
    /// </summary>
    public string Header { get; protected set; }
    public ICanBeParent? Parent { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    public ObservableCollection<IDataItem> Children { get; set; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="DataItem"/>> class with default values.
    /// </summary>
    public DataItem()
    {
        Name = string.Empty;
        Description = string.Empty;
        Header = string.Empty;
        PropertyChanged += SetHeader;
    }

    /// <summary>
    /// Sets the parent of this item
    /// </summary>
    public void SetParent(ICanBeParent? parent)
    {
        Parent?.TryRemoveChild(this);
        parent?.TryAddChild(this);
        Parent = parent;
    }

    /// <summary>
    /// Adds an Item to the <see cref="Children"/> collection of the parent of this item
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddSibling))]
    public void AddSibling(IDataItem? sibling)
    {
        if (sibling is not null && Parent is not null)
            sibling.SetParent(Parent);
    }
    protected bool CanAddSibling(IDataItem? sibling)
    {
        return Parent is not null && Parent.CanAddChild(sibling);
    }
    private void SetHeader(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Name) or nameof(Description))) return;
        Header = Description.Length > 0 ? $"{Name} - {Description}" : Name;
        OnPropertyChanged(nameof(Header));
    }
}