namespace SecsGemHelperClasses.Interfaces;

public interface IEventBus<T> :IDisposable
{
    void Publish(T item);
    IDisposable Subscribe(Action<T> handler);
    IDisposable Subscribe(Func<T, Task> handler);
    public IFilteredEventBus<TFilter> Filter<TFilter>(Func<TFilter, bool> filter) where TFilter : T;
    void Complete();
    void Error(Exception ex);
}