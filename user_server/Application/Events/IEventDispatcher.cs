using user_server.domain.events;

namespace user_server.application.events;

public interface IEventDispatcher
{
    Task DispatchAsync<TEvent>(TEvent @event) where TEvent : IDomainEvent;
    void RegisterHandler<TEvent>(IEventHandler<TEvent> handler) where TEvent : IDomainEvent;
}
