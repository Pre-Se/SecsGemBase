namespace SecsGemHelperClasses.Interfaces;
public interface IFilteredEventBus<out T>
{
    public IDisposable Subscribe(Action<T> handler);

    public IDisposable Subscribe(Func<T, Task> handler);
}
