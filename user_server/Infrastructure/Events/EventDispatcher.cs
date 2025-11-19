using Microsoft.Extensions.Logging;
using user_server.application.events;
using user_server.domain.events;

namespace user_server.infrastructure.events;

/// <summary>
/// In-memory implementation of event dispatcher
/// </summary>
public class EventDispatcher : IEventDispatcher
{
    private readonly Dictionary<Type, List<object>> _handlers = new();
    private readonly ILogger<EventDispatcher>? _logger;

    public EventDispatcher(ILogger<EventDispatcher>? logger = null)
    {
        _logger = logger;
    }

    public void RegisterHandler<TEvent>(IEventHandler<TEvent> handler) where TEvent : IDomainEvent
    {
        var eventType = typeof(TEvent);
        if (!_handlers.ContainsKey(eventType))
        {
            _handlers[eventType] = new List<object>();
        }

        _handlers[eventType].Add(handler);
        _logger?.LogDebug("Registered handler for event type {EventType}", eventType.Name);
    }

    public async Task DispatchAsync<TEvent>(TEvent @event) where TEvent : IDomainEvent
    {
        var eventType = typeof(TEvent);

        if (!_handlers.ContainsKey(eventType))
        {
            _logger?.LogDebug("No handlers registered for event type {EventType}", eventType.Name);
            return;
        }

        var handlers = _handlers[eventType].Cast<IEventHandler<TEvent>>().ToList();

        _logger?.LogInformation(
            "Dispatching event {EventType} to {HandlerCount} handler(s)",
            eventType.Name,
            handlers.Count
        );

        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(@event);
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Error handling event {EventType} with handler {HandlerType}",
                    eventType.Name,
                    handler.GetType().Name
                );
            }
        }
    }
}
