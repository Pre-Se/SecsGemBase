using Microsoft.Extensions.Logging;
using SecsGemMessageHandling.Events.Models;
using SecsGemMessageHandling.Events.Registry.Interface;
using System.Diagnostics.CodeAnalysis;
using SecsGemHelperClasses.Interfaces;
using SecsGemMessageHandling.Events.Registry.Events;

namespace SecsGemMessageHandling.Events.Registry;

public class SecsGemRegistry<T> : IRegistry<T> where T : IKeyedItem
{
    private readonly Dictionary<int, T> itemDictionary = [];
    private readonly IEventBus<RegistryChange<T>> registryChangeBus;
    private readonly ILogger<SecsGemRegistry<T>> logger;

    public SecsGemRegistry(ILogger<SecsGemRegistry<T>> logger, IEventBusFactory eventBusFactory)
    {
        this.logger = logger;
        registryChangeBus = eventBusFactory.CreateTransient<RegistryChange<T>>();
        OnAdded = registryChangeBus.Filter<RegistryChange<T>>(eventChange => eventChange.Type == RegistryChangeType.Added);
        OnUpdated = registryChangeBus.Filter<RegistryChange<T>>(eventChange => eventChange.Type == RegistryChangeType.Updated);
        OnRemoved = registryChangeBus.Filter<RegistryChange<T>>(eventChange => eventChange.Type == RegistryChangeType.Removed);
    }

    public IFilteredEventBus<RegistryChange<T>> OnAdded { get; }
    public IFilteredEventBus<RegistryChange<T>> OnUpdated { get; }
    public IFilteredEventBus<RegistryChange<T>> OnRemoved { get; }

    public IReadOnlyList<T> GetAll() => 
        itemDictionary.Values.ToList().AsReadOnly();
    
    public bool Add(T addedItem)
    {
        var id = addedItem.Id;
        if (itemDictionary.ContainsKey(id))
        {
            logger.LogError("{logPrefix} [{ceid}] already exist", T.LogPrefix, id);
            return false;
        }

        if (itemDictionary.TryAdd(id, addedItem))
        {
            var addedEvent = RegistryChange<T>.AddedEvent(addedItem);
            registryChangeBus.Publish(addedEvent);
            return true;
        }

        logger.LogError("{logPrefix} [{ceid}] was not added due to unknown error", T.LogPrefix, id);
        return false;
    }

    public bool Update(T newRegisteredItem, T oldRegisteredItem)
    {
        var oldCeid = oldRegisteredItem.Id;
        if (!itemDictionary.ContainsKey(oldCeid))
        {
            logger.LogError("{logPrefix} [{ceid}] doesn't exist, no changes will be made", T.LogPrefix, oldCeid);
            return false;
        }

        if (oldCeid != newRegisteredItem.Id)
        {
            itemDictionary.Remove(oldCeid);
        }

        itemDictionary[newRegisteredItem.Id] = newRegisteredItem;
        var eventUpdate = RegistryChange<T>.UpdatedEvent(newRegisteredItem, oldRegisteredItem);
        registryChangeBus.Publish(eventUpdate);
        return true;
    }

    public bool DeleteAll()
    {
        foreach (var item in GetAll())
        {
            Delete(item);
        }
        return true;
    }

    public bool Delete(int id)
    {
        if (TryGet(id, out var deleteItem) && itemDictionary.Remove(id))
        {
            var eventRemoved = RegistryChange<T>.RemovedEvent(deleteItem);
            registryChangeBus.Publish(eventRemoved);
            return true;
        }

        logger.LogError("{logPrefix} [{ceid}] doesn't exist, no changes will be made", T.LogPrefix, id);
        return false;
    }

    public bool Delete(T deleteItem)
    {
        var ceid = deleteItem.Id;
        if (itemDictionary.ContainsKey(ceid) && itemDictionary.Remove(ceid))
        {
            var eventRemoved = RegistryChange<T>.RemovedEvent(deleteItem);
            registryChangeBus.Publish(eventRemoved);
            return true;
        }

        logger.LogError("{logPrefix} [{ceid}] doesn't exist, no changes will be made", T.LogPrefix, ceid);
        return false;
    }

    public bool TryGet(int id, [NotNullWhen(true)] out T? registeredItem)
    {
        return itemDictionary.TryGetValue(id, out registeredItem);
    }

    public bool Contains(int id)
    {
        return itemDictionary.ContainsKey(id);
    }
}
