using System.Collections.ObjectModel;
using System.ComponentModel;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Interfaces;

namespace SecsGemBaseItems.LibraryManager;

public interface ISecsGemLibraryManager : INotifyPropertyChanged
{
    /// <inheritdoc cref="EvenCoolerFastSim.WPF.LibraryManager.SecsGemLibraryManager.Library" />
    ObservableCollection<SecsGemTransaction> Library { get; }

    /// <inheritdoc cref="EvenCoolerFastSim.WPF.LibraryManager.SecsGemLibraryManager.SelectedItem" />
    IDataItem? SelectedItem { get; set; }

    /// <inheritdoc cref="EvenCoolerFastSim.WPF.LibraryManager.SecsGemLibraryManager.OpenLibrary()"/>
    void OpenLibrary();

    /// <inheritdoc cref="EvenCoolerFastSim.WPF.LibraryManager.SecsGemLibraryManager.AddItemToLibrary(IDataItem)"/>
    void AddItemToLibrary(IDataItem item);
}
