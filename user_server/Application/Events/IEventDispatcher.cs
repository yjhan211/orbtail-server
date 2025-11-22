using user_server.domain.events;

namespace user_server.application.events;

/// <summary>
/// Interface for dispatching domain events
/// </summary>
public interface IEventDispatcher
{
    /// <summary>
    /// Dispatches an event to all registered handlers
    /// </summary>
    /// <typeparam name="TEvent">Type of event</typeparam>
    /// <param name="event">The event to dispatch</param>
    Task DispatchAsync<TEvent>(TEvent @event) where TEvent : IDomainEvent;

    /// <summary>
    /// Registers an event handler
    /// </summary>
    /// <typeparam name="TEvent">Type of event</typeparam>
    /// <param name="handler">The handler to register</param>
    void RegisterHandler<TEvent>(IEventHandler<TEvent> handler) where TEvent : IDomainEvent;
}
