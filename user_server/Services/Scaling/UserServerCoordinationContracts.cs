using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MessagePack;
using network.contracts.scaling;

namespace user_server.services.scaling;

public sealed record UserSessionOwner(
    long PlayerId,
    string NodeId,
    string NodeGeneration,
    string SessionId,
    long SessionGeneration)
{
    public bool IsValid =>
        PlayerId > 0 &&
        UserServerClusterOptions.IsSafeNodeId(NodeId) &&
        UserServerClusterOptions.IsSafeTokenComponent(NodeGeneration) &&
        UserServerClusterOptions.IsSafeTokenComponent(SessionId) &&
        SessionGeneration > 0;

    public string Token => UserServerScalingKeys.FormatSessionOwnerToken(
        NodeId,
        NodeGeneration,
        SessionId,
        SessionGeneration);
}

public sealed record UserSessionOwnerAcquisition(
    UserSessionOwner CurrentOwner,
    UserSessionOwner? PreviousOwner,
    bool WasAlreadyOwned);

public sealed record MatchingLeaderLease(
    string NodeId,
    string NodeGeneration,
    long Fence)
{
    public bool IsValid =>
        UserServerClusterOptions.IsSafeNodeId(NodeId) &&
        UserServerClusterOptions.IsSafeTokenComponent(NodeGeneration) &&
        Fence > 0;

    public string Token => UserServerScalingKeys.FormatMatchingLeaderToken(NodeId, NodeGeneration, Fence);
}

public enum MatchingLifecycleEffect
{
    PlayerLeft = 1,
    PlayerCompleted = 2,
    PlayerAdmissionFailed = 3,
    PlayerReleased = 4
}

public sealed record MatchingLifecycleApplyResult(
    bool WasAppliedNow,
    bool MatchingClaimReleased,
    MatchingLifecycleEffect EffectiveEffect,
    string EffectiveEventFingerprint,
    bool HasConflictingTerminalEvent);

public enum MatchingAdmissionCleanupMode
{
    KeepQueue = 0,
    AbortAndNotify = 1
}

[MessagePackObject]
public sealed class MatchingAdmissionRecoveryRoute
{
    [Key(0)] public long PlayerId { get; set; }
    [Key(1)] public string OwnerNodeId { get; set; } = string.Empty;
    [Key(2)] public string OwnerNodeGeneration { get; set; } = string.Empty;
    [Key(3)] public string OwnerSessionId { get; set; } = string.Empty;
    [Key(4)] public long OwnerSessionGeneration { get; set; }
    [Key(5)] public string RequestId { get; set; } = string.Empty;
    [Key(6)] public byte[] QueueEntry { get; set; } = Array.Empty<byte>();

    [IgnoreMember]
    public bool HasDistributedOwner =>
        !string.IsNullOrWhiteSpace(OwnerNodeId) &&
        !string.IsNullOrWhiteSpace(OwnerNodeGeneration) &&
        !string.IsNullOrWhiteSpace(OwnerSessionId) &&
        OwnerSessionGeneration > 0;

    [IgnoreMember]
    public bool IsValid =>
        PlayerId > 0 &&
        UserServerClusterOptions.IsSafeTokenComponent(RequestId) &&
        QueueEntry.Length > 0 &&
        (HasDistributedOwner
            ? UserServerClusterOptions.IsSafeNodeId(OwnerNodeId) &&
              UserServerClusterOptions.IsSafeTokenComponent(OwnerNodeGeneration) &&
              UserServerClusterOptions.IsSafeTokenComponent(OwnerSessionId)
            : string.IsNullOrEmpty(OwnerNodeId) &&
              string.IsNullOrEmpty(OwnerNodeGeneration) &&
              string.IsNullOrEmpty(OwnerSessionId) &&
              OwnerSessionGeneration == 0);

    public UserSessionOwner? GetSessionOwner() =>
        HasDistributedOwner
            ? new UserSessionOwner(
            PlayerId,
            OwnerNodeId,
            OwnerNodeGeneration,
            OwnerSessionId,
            OwnerSessionGeneration)
            : null;
}

[MessagePackObject]
public sealed class MatchingAdmissionRecoveryRecord
{
    [Key(0)] public long MatchingId { get; set; }
    [Key(1)] public long DeadlineUnixMilliseconds { get; set; }
    [Key(2)] public List<MatchingAdmissionRecoveryRoute> Players { get; set; } = new();
    [Key(3)] public string GameServerNodeId { get; set; } = string.Empty;
    [Key(4)] public string GameServerGeneration { get; set; } = string.Empty;
    [Key(5)] public long GameServerFence { get; set; }
    [Key(6)] public bool AdmissionCompleted { get; set; }
    [Key(7)] public long GameServerOwnerLossObservedUnixMilliseconds { get; set; }
    [Key(8)] public MatchingAdmissionCleanupMode CleanupMode { get; set; }

    [IgnoreMember]
    public bool HasGameServerOwner =>
        !string.IsNullOrWhiteSpace(GameServerNodeId) &&
        !string.IsNullOrWhiteSpace(GameServerGeneration) &&
        GameServerFence > 0;

    [IgnoreMember]
    public bool IsValid =>
        MatchingId > 0 &&
        DeadlineUnixMilliseconds > DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds() &&
        Players is { Count: > 0 and <= 64 } &&
        Players.All(player => player is { IsValid: true }) &&
        Players.Select(player => player.PlayerId).Distinct().Count() == Players.Count &&
        Enum.IsDefined(CleanupMode) &&
        GameServerOwnerLossObservedUnixMilliseconds >= 0 &&
        (GetGameServerOwner() is { IsValid: true } ||
         (string.IsNullOrEmpty(GameServerNodeId) &&
          string.IsNullOrEmpty(GameServerGeneration) &&
          GameServerFence == 0));

    public GameServerMatchOwner? GetGameServerOwner() =>
        HasGameServerOwner
            ? new GameServerMatchOwner(
                MatchingId,
                GameServerNodeId,
                GameServerGeneration,
                GameServerFence)
            : null;
}

public interface IUserServerCoordinationStore
{
    public Task<bool> TryAcquireNodeLeaseAsync(UserServerProcessIdentity identity, TimeSpan lifetime);
    public Task<bool> RenewNodeLeaseAsync(UserServerProcessIdentity identity, TimeSpan lifetime);
    public Task<bool> ReleaseNodeLeaseAsync(UserServerProcessIdentity identity);

    public Task<UserSessionOwnerAcquisition?> TryAcquireOrReplaceSessionOwnerAsync(
        UserServerProcessIdentity identity,
        long playerId,
        string sessionId,
        TimeSpan lifetime);

    public Task<bool> RenewSessionOwnerAsync(UserSessionOwner owner, TimeSpan lifetime);
    public Task<UserSessionOwner?> GetSessionOwnerAsync(long playerId);
    public Task<bool> ReleaseSessionOwnerAsync(UserSessionOwner owner);
    public Task<bool> RestorePreviousSessionOwnerAsync(
        UserSessionOwnerAcquisition acquisition,
        TimeSpan lifetime);

    public Task<MatchingLeaderLease?> TryAcquireMatchingLeaderAsync(
        UserServerProcessIdentity identity,
        TimeSpan lifetime);

    public Task<bool> RenewMatchingLeaderAsync(MatchingLeaderLease lease, TimeSpan lifetime);
    public Task<MatchingLeaderLease?> GetMatchingLeaderAsync();
    public Task<bool> ReleaseMatchingLeaderAsync(MatchingLeaderLease lease);

    public Task<bool> TryRegisterMatchingAdmissionRecoveryAsync(
        MatchingLeaderLease? lease,
        MatchingAdmissionRecoveryRecord record,
        TimeSpan lifetime);

    public Task<IReadOnlyList<long>> GetExpiredMatchingAdmissionRecoveryIdsAsync(
        DateTimeOffset now,
        int maximumCount);

    public Task<MatchingAdmissionRecoveryRecord?> GetMatchingAdmissionRecoveryAsync(long matchingId);
    public Task<bool> TryUpdateMatchingAdmissionRecoveryAsync(
        MatchingLeaderLease? lease,
        MatchingAdmissionRecoveryRecord expectedRecord,
        MatchingAdmissionRecoveryRecord updatedRecord,
        TimeSpan lifetime);

    public Task RemoveMatchingAdmissionRecoveryAsync(long matchingId);
    public Task<DateTimeOffset> GetRedisTimeAsync();

    public Task<MatchingLifecycleApplyResult> ApplyMatchingLifecycleOnceAsync(
        string eventId,
        MatchingLifecycleEffect effect,
        long playerId,
        long matchingId,
        DateTimeOffset occurredAt,
        TimeSpan markerLifetime);
}

public static class UserServerScalingKeys
{
    private const string Prefix = "{user-server-scaling}:";

    public static string NodeLease(string nodeId) => $"{Prefix}node:{nodeId}:lease";
    public static string SessionOwner(long playerId) =>
        $"{Prefix}session:{playerId.ToString(CultureInfo.InvariantCulture)}:owner";

    public static string SessionEpoch(long playerId) =>
        $"{Prefix}session:{playerId.ToString(CultureInfo.InvariantCulture)}:epoch";

    public static string SessionAcquireReceipt(long playerId, string sessionId) =>
        $"{Prefix}session:{playerId.ToString(CultureInfo.InvariantCulture)}:acquire:{sessionId}";

    public static string MatchingLeader => $"{Prefix}matching:leader";
    public static string MatchingLeaderFence => $"{Prefix}matching:leader:fence";
    public static string MatchingAdmissionRecovery(long matchingId) =>
        $"{Prefix}matching:{matchingId.ToString(CultureInfo.InvariantCulture)}:admission-recovery";

    public static string MatchingAdmissionRecoveryDeadlines =>
        $"{Prefix}matching:admission-recovery-deadlines";
    public static string MatchingLifecycleTerminal(long playerId, long matchingId) =>
        $"{Prefix}lifecycle:{matchingId.ToString(CultureInfo.InvariantCulture)}:" +
        $"{playerId.ToString(CultureInfo.InvariantCulture)}:terminal";

    public static string FormatSessionOwnerToken(
        string nodeId,
        string nodeGeneration,
        string sessionId,
        long sessionGeneration) =>
        $"{nodeId}|{nodeGeneration}|{sessionId}|{sessionGeneration.ToString(CultureInfo.InvariantCulture)}";

    public static string FormatMatchingLeaderToken(string nodeId, string nodeGeneration, long fence) =>
        $"{nodeId}|{nodeGeneration}|{fence.ToString(CultureInfo.InvariantCulture)}";

    public static string FingerprintEventId(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventId))).ToLowerInvariant();
    }

    public static bool TryParseSessionOwnerToken(
        long playerId,
        string? token,
        out UserSessionOwner? owner)
    {
        owner = null;
        if (playerId <= 0 || string.IsNullOrWhiteSpace(token))
            return false;

        string[] parts = token.Split('|');
        if (parts.Length != 4 ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long generation))
            return false;

        var candidate = new UserSessionOwner(playerId, parts[0], parts[1], parts[2], generation);
        if (!candidate.IsValid)
            return false;

        owner = candidate;
        return true;
    }

    public static bool TryParseMatchingLeaderToken(string? token, out MatchingLeaderLease? lease)
    {
        lease = null;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        string[] parts = token.Split('|');
        if (parts.Length != 3 ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long fence))
            return false;

        var candidate = new MatchingLeaderLease(parts[0], parts[1], fence);
        if (!candidate.IsValid)
            return false;

        lease = candidate;
        return true;
    }
}
