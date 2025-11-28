using Microsoft.Extensions.Logging;
using user_server.application.events;
using user_server.domain.events;

namespace user_server.infrastructure.events;

public class EventDispatcher(ILogger<EventDispatcher>? logger = null) : IEventDispatcher
{
    private readonly Dictionary<Type, List<object>> _handlers = new();

    public void RegisterHandler<TEvent>(IEventHandler<TEvent> handler) where TEvent : IDomainEvent
    {
        var eventType = typeof(TEvent);
        if (!_handlers.ContainsKey(eventType))
        {
            _handlers[eventType] = [];
        }

        _handlers[eventType].Add(handler);
        logger?.LogDebug("Registered handler for event type {EventType}", eventType.Name);
    }

    public async Task DispatchAsync<TEvent>(TEvent @event) where TEvent : IDomainEvent
    {
        var eventType = typeof(TEvent);
        if (!_handlers.TryGetValue(eventType, out var eventHandler))
        {
            logger?.LogDebug("No handlers registered for event type {EventType}", eventType.Name);
            return;
        }

        var handlers = eventHandler.Cast<IEventHandler<TEvent>>().ToList();
        logger?.LogInformation("Dispatching event {EventType} to {HandlerCount} handler(s)", eventType.Name, handlers.Count);
        
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(@event);
            }
            catch (Exception ex)
            {
                logger?.LogError(
                    ex,
                    "Error handling event {EventType} with handler {HandlerType}",
                    eventType.Name,
                    handler.GetType().Name
                );
            }
        }
    }
}
