namespace SecsGemHelperClasses.Interfaces;
public interface IEventBusFactory
{
    public IEventBus<T> CreateTransient<T>() where T : notnull;
}
