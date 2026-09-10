using Microsoft.Extensions.Logging;
using SecsGemHelperClasses.Interfaces;

namespace SecsGemHelperClasses;
public class EventBusFactory(ILoggerFactory provider) : IEventBusFactory
{
    public IEventBus<T> CreateTransient<T>() where T : notnull
    {
        return new EventBus<T>(provider.CreateLogger<EventBus<T>>());
    }
}
