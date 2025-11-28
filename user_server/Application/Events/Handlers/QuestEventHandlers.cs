using Microsoft.Extensions.Logging;
using user_server.domain.events;

namespace user_server.application.events.handlers;

public class QuestCompletedEventHandler(ILogger<QuestCompletedEventHandler>? logger = null)
    : IEventHandler<QuestCompletedEvent>
{
    public Task HandleAsync(QuestCompletedEvent @event)
    {
        logger?.LogInformation(
            "Quest {QuestId} completed by player {PlayerId} at {OccurredAt}",
            @event.QuestId,
            @event.PlayerId,
            @event.OccurredAt
        );

        // Future: Check for quest chains, trigger follow-up quests
        // Future: Award achievements
        // Future: Update statistics

        return Task.CompletedTask;
    }
}

public class QuestStartedEventHandler(ILogger<QuestStartedEventHandler>? logger = null)
    : IEventHandler<QuestStartedEvent>
{
    public Task HandleAsync(QuestStartedEvent @event)
    {
        logger?.LogInformation(
            "Quest {QuestId} started by player {PlayerId} at {OccurredAt}",
            @event.QuestId,
            @event.PlayerId,
            @event.OccurredAt
        );

        // Future: Send tutorial hints for new players
        // Future: Update quest tracking UI

        return Task.CompletedTask;
    }
}
