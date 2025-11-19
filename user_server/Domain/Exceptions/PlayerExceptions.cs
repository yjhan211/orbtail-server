namespace user_server.domain.exceptions;

/// <summary>
/// Exception thrown when a player is not found
/// </summary>
public class PlayerNotFoundException : DomainException
{
    public long PlayerId { get; }

    public PlayerNotFoundException(long playerId)
        : base("PLAYER_NOT_FOUND", $"Player with ID {playerId} not found")
    {
        PlayerId = playerId;
    }
}

/// <summary>
/// Exception thrown when player has insufficient resources
/// </summary>
public class InsufficientResourceException : DomainException
{
    public string ResourceType { get; }
    public int Required { get; }
    public int Available { get; }

    public InsufficientResourceException(string resourceType, int required, int available)
        : base("INSUFFICIENT_RESOURCE",
            $"Insufficient {resourceType}: required {required}, available {available}")
    {
        ResourceType = resourceType;
        Required = required;
        Available = available;
    }
}

/// <summary>
/// Exception thrown when player is in invalid state for operation
/// </summary>
public class InvalidPlayerStateException : DomainException
{
    public string CurrentState { get; }
    public string RequiredState { get; }

    public InvalidPlayerStateException(string currentState, string requiredState)
        : base("INVALID_PLAYER_STATE",
            $"Invalid player state: current={currentState}, required={requiredState}")
    {
        CurrentState = currentState;
        RequiredState = requiredState;
    }
}

/// <summary>
/// Exception thrown when player is dead
/// </summary>
public class PlayerDeadException : DomainException
{
    public long PlayerId { get; }

    public PlayerDeadException(long playerId)
        : base("PLAYER_DEAD", $"Player {playerId} is dead and cannot perform this action")
    {
        PlayerId = playerId;
    }
}
