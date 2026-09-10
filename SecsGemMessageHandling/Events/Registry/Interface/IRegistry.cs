using SecsGemHelperClasses.Interfaces;
using SecsGemMessageHandling.Events.Models;
using SecsGemMessageHandling.Events.Registry.Events;
using System.Diagnostics.CodeAnalysis;

namespace SecsGemMessageHandling.Events.Registry.Interface;

public interface IRegistry<T> where T : IKeyedItem
{
    public IReadOnlyList<T> GetAll();
    public IFilteredEventBus<RegistryChange<T>> OnAdded { get; }
    public IFilteredEventBus<RegistryChange<T>> OnUpdated { get; }
    public IFilteredEventBus<RegistryChange<T>> OnRemoved { get; }
    bool Add(T addedItem);
    bool Update(T newRegisteredItem, T oldRegisteredItem);
    bool DeleteAll();
    bool Delete(int deleteItem);
    bool Delete(T deleteItem);
    bool TryGet(int id, [NotNullWhen(true)] out T? registeredItem);
    bool Contains(int id);
}