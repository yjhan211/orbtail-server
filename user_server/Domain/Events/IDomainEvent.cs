namespace user_server.domain.events;

/// <summary>
/// Base interface for all domain events
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// Timestamp when the event occurred
    /// </summary>
    DateTime OccurredAt { get; }

    /// <summary>
    /// ID of the player who triggered the event
    /// </summary>
    long PlayerId { get; }
}
