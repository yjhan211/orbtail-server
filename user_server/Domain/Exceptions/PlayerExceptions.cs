namespace user_server.domain.exceptions;

/// <summary>
/// Exception thrown when a player is not found
/// </summary>
public class PlayerNotFoundException(long playerId)
    : DomainException("PLAYER_NOT_FOUND", $"Player with ID {playerId} not found")
{
    public long PlayerId { get; } = playerId;
}

/// <summary>
/// Exception thrown when player has insufficient resources
/// </summary>
public class InsufficientResourceException(string resourceType, int required, int available) : DomainException(
    "INSUFFICIENT_RESOURCE",
    $"Insufficient {resourceType}: required {required}, available {available}")
{
    public string ResourceType { get; } = resourceType;
    public int Required { get; } = required;
    public int Available { get; } = available;
}

/// <summary>
/// Exception thrown when player is in invalid state for operation
/// </summary>
public class InvalidPlayerStateException(string currentState, string requiredState) : DomainException(
    "INVALID_PLAYER_STATE",
    $"Invalid player state: current={currentState}, required={requiredState}")
{
    public string CurrentState { get; } = currentState;
    public string RequiredState { get; } = requiredState;
}

/// <summary>
/// Exception thrown when player is dead
/// </summary>
public class PlayerDeadException(long playerId)
    : DomainException("PLAYER_DEAD", $"Player {playerId} is dead and cannot perform this action")
{
    public long PlayerId { get; } = playerId;
}
