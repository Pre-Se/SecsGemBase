using System.Numerics;

namespace SecsGemMessageHandling.Events.Registry.Events;

public record RegistryChange<T> : IEqualityOperators<RegistryChange<T>, RegistryChange<T>, bool>
{
    private RegistryChange(RegistryChangeType Type,
        T NewEvent,
        T? OldEvent = default)
    {
        this.Type = Type;
        this.NewEvent = NewEvent;
        this.OldEvent = OldEvent;
    }

    public static RegistryChange<T> AddedEvent(T newEvent)
    {
        return new RegistryChange<T>(RegistryChangeType.Added, newEvent);
    }

    public static RegistryChange<T> UpdatedEvent(T newEvent, T oldEvent)
    {
        return new RegistryChange<T>(RegistryChangeType.Updated, newEvent, oldEvent);
    }

    public static RegistryChange<T> RemovedEvent(T removedEvent)
    {
        return new RegistryChange<T>(RegistryChangeType.Removed, removedEvent);
    }
    public RegistryChangeType Type { get; init; }
    public T NewEvent { get; init; }
    public T? OldEvent { get; init; }
}

public enum RegistryChangeType { Added, Updated, Removed }