using Microsoft.Extensions.Logging;
using System.Reactive.Linq;
using SecsGemHelperClasses.Interfaces;

namespace SecsGemHelperClasses;

public partial class EventBus<T>
{
    public class FilteredEventBus<TFilter>(EventBus<T> eventBus, Func<TFilter, bool> condition, ILogger<EventBus<T>> logger) 
        : IFilteredEventBus<TFilter> where TFilter : T 
    {
        private IObservable<TFilter> Events => eventBus.Events
        .Where(e => e is TFilter)
        .Select(e => (TFilter)e)
        .Where(condition);

        public IDisposable Subscribe(Action<TFilter> handler)
        {
            var subscription = Events.Subscribe(handler);

            return eventBus.AddSubscription(subscription);
        }

        public IDisposable Subscribe(Func<TFilter, Task> handler)
        {
            var subscription = Events
                .Select(item => Observable.FromAsync(() => handler(item)))
                .Concat()
                .Subscribe(
                    _ => { },
                    onError => { logger.LogError("{error}", onError.Message); }
                );

            return eventBus.AddSubscription(subscription);
        }
    }
}

