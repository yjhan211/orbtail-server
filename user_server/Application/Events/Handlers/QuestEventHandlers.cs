using Microsoft.Extensions.Logging;
using user_server.domain.events;

namespace user_server.application.events.handlers;

/// <summary>
/// Handler for quest completion events
/// Logs quest completion and could trigger rewards, achievements, etc.
/// </summary>
public class QuestCompletedEventHandler : IEventHandler<QuestCompletedEvent>
{
    private readonly ILogger<QuestCompletedEventHandler>? _logger;

    public QuestCompletedEventHandler(ILogger<QuestCompletedEventHandler>? logger = null)
    {
        _logger = logger;
    }

    public Task HandleAsync(QuestCompletedEvent @event)
    {
        _logger?.LogInformation(
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

/// <summary>
/// Handler for quest started events
/// </summary>
public class QuestStartedEventHandler : IEventHandler<QuestStartedEvent>
{
    private readonly ILogger<QuestStartedEventHandler>? _logger;

    public QuestStartedEventHandler(ILogger<QuestStartedEventHandler>? logger = null)
    {
        _logger = logger;
    }

    public Task HandleAsync(QuestStartedEvent @event)
    {
        _logger?.LogInformation(
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
