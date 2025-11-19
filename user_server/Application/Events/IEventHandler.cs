using user_server.domain.events;

namespace user_server.application.events;

/// <summary>
/// Interface for event handlers
/// </summary>
/// <typeparam name="TEvent">Type of event to handle</typeparam>
public interface IEventHandler<in TEvent> where TEvent : IDomainEvent
{
    /// <summary>
    /// Handles the event asynchronously
    /// </summary>
    /// <param name="event">The event to handle</param>
    Task HandleAsync(TEvent @event);
}
