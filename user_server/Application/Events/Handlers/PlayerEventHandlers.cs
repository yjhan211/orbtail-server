using Microsoft.Extensions.Logging;
using user_server.domain.events;

namespace user_server.application.events.handlers;

/// <summary>
/// Handler for player info changed events
/// Could be used for analytics, caching, or broadcasting to other systems
/// </summary>
public class PlayerInfoChangedEventHandler : IEventHandler<PlayerInfoChangedEvent>
{
    private readonly ILogger<PlayerInfoChangedEventHandler>? _logger;

    public PlayerInfoChangedEventHandler(ILogger<PlayerInfoChangedEventHandler>? logger = null)
    {
        _logger = logger;
    }

    public Task HandleAsync(PlayerInfoChangedEvent @event)
    {
        _logger?.LogDebug(
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

/// <summary>
/// Handler for player damage events
/// Could track combat statistics, achievements, etc.
/// </summary>
public class PlayerDamagedEventHandler : IEventHandler<PlayerDamagedEvent>
{
    private readonly ILogger<PlayerDamagedEventHandler>? _logger;

    public PlayerDamagedEventHandler(ILogger<PlayerDamagedEventHandler>? logger = null)
    {
        _logger = logger;
    }

    public Task HandleAsync(PlayerDamagedEvent @event)
    {
        _logger?.LogInformation(
            "Player {PlayerId} took {DamageAmount} {DamageType} damage, HP: {RemainingHp}",
            @event.PlayerId,
            @event.DamageAmount,
            @event.DamageType,
            @event.RemainingHp
        );

        // Future: Track combat statistics
        // Future: Check for death and respawn
        // Future: Update combat UI

        return Task.CompletedTask;
    }
}
