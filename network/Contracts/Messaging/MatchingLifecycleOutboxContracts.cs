using System.Security.Cryptography;
using System.Text;
using MessagePack;
using network.common;

namespace network.contracts.messaging;

[MessagePackObject]
public sealed class MatchingLifecycleOutboxRecord
{
    public const int CurrentVersion = 1;
    public const int MaximumPayloadBytes = 64 * 1024;

    [Key(0)] public int Version { get; set; } = CurrentVersion;
    [Key(1)] public string EventId { get; set; } = string.Empty;
    [Key(2)] public string EventIdFingerprint { get; set; } = string.Empty;
    [Key(3)] public string Subject { get; set; } = string.Empty;
    [Key(4)] public byte[] Payload { get; set; } = Array.Empty<byte>();
    [Key(5)] public long PlayerId { get; set; }
    [Key(6)] public long MatchingId { get; set; }

    [IgnoreMember]
    public bool IsValid =>
        Version == CurrentVersion &&
        PlayerId > 0 &&
        MatchingId > 0 &&
        !string.IsNullOrWhiteSpace(EventId) &&
        EventId.Length <= 512 &&
        MatchingLifecycleOutboxKeys.IsAllowedSubject(Subject) &&
        string.Equals(
            EventId,
            MatchingLifecycleMessageIds.Create(Subject, PlayerId, MatchingId),
            StringComparison.Ordinal) &&
        MatchingLifecycleOutboxKeys.IsValidFingerprint(EventIdFingerprint) &&
        string.Equals(
            EventIdFingerprint,
            MatchingLifecycleOutboxKeys.FingerprintEventId(EventId),
            StringComparison.Ordinal) &&
        Payload is { Length: > 0 and <= MaximumPayloadBytes };
}

public static class MatchingLifecycleOutboxKeys
{
    private const string Prefix = "{matching-lifecycle-outbox}:";

    public static string Due => $"{Prefix}due";

    public static string AbortFence(long playerId, long matchingId)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));
        return $"{Prefix}abort:{matchingId}:{playerId}";
    }

    public static string Published(string eventIdFingerprint)
    {
        if (!IsValidFingerprint(eventIdFingerprint))
            throw new ArgumentException("A SHA-256 event-id fingerprint is required.", nameof(eventIdFingerprint));
        return $"{Prefix}published:{eventIdFingerprint}";
    }

    public static string Record(string eventIdFingerprint)
    {
        if (!IsValidFingerprint(eventIdFingerprint))
            throw new ArgumentException("A SHA-256 event-id fingerprint is required.", nameof(eventIdFingerprint));
        return $"{Prefix}record:{eventIdFingerprint}";
    }

    public static string FingerprintEventId(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventId)));
    }

    public static bool IsValidFingerprint(string? value)
    {
        return value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
    }

    public static bool IsAllowedSubject(string? subject)
    {
        return subject is
            MatchingLifecycleSubjects.PlayerLeft or
            MatchingLifecycleSubjects.PlayerCompleted or
            MatchingLifecycleSubjects.PlayerAdmissionFailed or
            MatchingLifecycleSubjects.PlayerReleased;
    }
}
