using user_server.domain.events;

namespace user_server.application.events;

public interface IEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent @event);
}
