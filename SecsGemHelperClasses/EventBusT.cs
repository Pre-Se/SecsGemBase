using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SecsGemHelperClasses.Interfaces;

namespace SecsGemHelperClasses;

public partial class EventBus<T>(ILogger<EventBus<T>> logger) : IEventBus<T>, IDisposable where T : notnull
{
    private readonly Subject<T> subject = new();
    private readonly CompositeDisposable subscriptions = [];
    private readonly Channel<T> channel = Channel.CreateUnbounded<T>(
        new UnboundedChannelOptions { SingleReader = true });

    private Task? _consumer;
    private readonly Lock _startLock = new();

    public IObservable<T> Events => subject.ObserveOn(ThreadPoolScheduler.Instance);

    public void Publish(T item)
    {
        EnsureConsumerStarted();
        channel.Writer.TryWrite(item);
    }

    private void EnsureConsumerStarted()
    {
        if (_consumer is not null) return;
        lock (_startLock)
        {
            if (_consumer is not null) return;
            _consumer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in channel.Reader.ReadAllAsync())
                    {
                        try { subject.OnNext(item); }
                        catch (Exception ex) { logger.LogError(ex, "EventBus consumer error"); }
                    }
                }
                catch { }
            });
        }
    }

    public IDisposable Subscribe(Action<T> handler)
    {
        var subscription = Events.Subscribe(handler);

        return AddSubscription(subscription);
    }

    public IDisposable Subscribe(Func<T, Task> handler)
    {
        var subscription = Events
            .Select(item => Observable.FromAsync(() => handler(item)))
            .Concat()
            .Subscribe(
                _ => { },
                onError => { logger.LogError("{error}", onError.Message); }
            );

        return AddSubscription(subscription);
    }

    private IDisposable AddSubscription(IDisposable subscription)
    {
        subscriptions.Add(subscription);
        return subscription;
    }

    public IFilteredEventBus<TFilter> Filter<TFilter>(Func<TFilter, bool> filter) where TFilter : T
    {
        return new FilteredEventBus<TFilter>(this, filter, logger);
    }

    public void Complete() => subject.OnCompleted();

    public void Error(Exception ex) => subject.OnError(ex);

    public void Dispose()
    {
        channel.Writer.TryComplete();
        try { _consumer?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        Complete();
        subscriptions.Dispose();
        subject.Dispose();
        GC.SuppressFinalize(this);
    }
}
