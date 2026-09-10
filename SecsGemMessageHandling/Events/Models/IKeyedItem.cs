namespace SecsGemMessageHandling.Events.Models;

/// <summary>
/// Used for any item that needs an ID
/// </summary>
public interface IKeyedItem
{
    int Id { get; }
    static abstract string LogPrefix { get; }
}
