using MessagePack;

namespace network.contracts.messaging;

[MessagePackObject]
public sealed class MatchingLifecycleEnvelope
{
    public const int CurrentVersion = 1;

    [Key(0)]
    public int Version { get; set; } = CurrentVersion;

    [Key(1)]
    public long PlayerId { get; init; }

    [Key(2)]
    public long MatchingId { get; init; }

    [Key(3)]
    public long OccurredAtUnixMilliseconds { get; init; }

    [Key(4)]
    public string EventId { get; set; } = string.Empty;

    [IgnoreMember]
    public bool IsValid =>
        Version == CurrentVersion &&
        PlayerId > 0 &&
        MatchingId > 0 &&
        OccurredAtUnixMilliseconds > 0 &&
        !string.IsNullOrWhiteSpace(EventId);
}

public static class MatchingLifecycleMessageIds
{
    public static string Create(string subject, long playerId, long matchingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));

        return $"{subject}:{matchingId}:{playerId}";
    }
}
