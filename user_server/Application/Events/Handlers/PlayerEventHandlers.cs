using Microsoft.Extensions.Logging;
using user_server.domain.events;

namespace user_server.application.events.handlers;

public class PlayerInfoChangedEventHandler(ILogger<PlayerInfoChangedEventHandler>? logger = null)
    : IEventHandler<PlayerInfoChangedEvent>
{
    public Task HandleAsync(PlayerInfoChangedEvent @event)
    {
        logger?.LogDebug(
            "Player info changed for player {PlayerId} at {OccurredAt}",
            @event.PlayerId,
            @event.OccurredAt
        );

        // Future: Update analytics
        // Future: Invalidate caches
        // Future: Broadcast to monitoring systems

        return Task.CompletedTask;
    }
}
