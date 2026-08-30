using MessagePack;

namespace network.contracts.scaling;

public enum GameServerNodeStatus
{
    Starting = 0,
    Accepting = 1,
    Draining = 2
}

[MessagePackObject]
public sealed class GameServerNodeDescriptor
{
    [Key("nodeId")] public string NodeId { get; set; } = string.Empty;
    [Key("generation")] public string Generation { get; set; } = string.Empty;
    [Key("publicHost")] public string PublicHost { get; set; } = string.Empty;
    [Key("publicPort")] public int PublicPort { get; set; }
    [Key("maxConcurrentMatches")] public int MaxConcurrentMatches { get; set; }
    [Key("activeMatchCount")] public int ActiveMatchCount { get; set; }
    [Key("status")] public GameServerNodeStatus Status { get; set; }
    [Key("heartbeatUnixMilliseconds")] public long HeartbeatUnixMilliseconds { get; set; }
    [Key("routingProtocolVersion")] public int RoutingProtocolVersion { get; set; } = 1;
}

public sealed record GameServerNodeIdentity(string NodeId, string Generation)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(NodeId) &&
        !string.IsNullOrWhiteSpace(Generation);
}

public sealed record GameServerMatchOwner(
    long MatchingId,
    string NodeId,
    string Generation,
    long Fence)
{
    public bool IsValid =>
        MatchingId > 0 &&
        !string.IsNullOrWhiteSpace(NodeId) &&
        !string.IsNullOrWhiteSpace(Generation) &&
        Fence > 0;

    public string Token => GameServerRoutingKeys.FormatOwnerToken(NodeId, Generation, Fence);
}

public sealed class GameServerScalingOptions
{
    public bool Enabled { get; init; }
    public string NodeId { get; init; } = string.Empty;
    public string PublicHost { get; init; } = string.Empty;
    public int PublicPort { get; init; } = 9001;
    public int MaxConcurrentMatches { get; init; } = 100;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan NodeLeaseLifetime { get; init; } = TimeSpan.FromSeconds(12);
    public TimeSpan ReservationLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ActiveOwnerLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (!Enabled)
            return;
        if (string.IsNullOrWhiteSpace(NodeId) || NodeId.Contains('|'))
            throw new InvalidOperationException(
                "horizontalScaling:nodeId is required and cannot contain '|' when horizontal scaling is enabled.");
        if (string.IsNullOrWhiteSpace(PublicHost))
            throw new InvalidOperationException(
                "horizontalScaling:publicHost is required when horizontal scaling is enabled.");
        if (PublicPort is <= 0 or > ushort.MaxValue)
            throw new InvalidOperationException("horizontalScaling:publicPort must be between 1 and 65535.");
        if (MaxConcurrentMatches <= 0)
            throw new InvalidOperationException("horizontalScaling:maxConcurrentMatches must be greater than zero.");
        if (HeartbeatInterval <= TimeSpan.Zero || NodeLeaseLifetime <= HeartbeatInterval * 2)
            throw new InvalidOperationException(
                "horizontalScaling:nodeLeaseSeconds must exceed twice the heartbeat interval.");
        if (ReservationLifetime <= TimeSpan.Zero || ActiveOwnerLifetime <= ReservationLifetime)
            throw new InvalidOperationException(
                "horizontalScaling owner lifetimes are invalid; active lifetime must exceed reservation lifetime.");
        if (DrainTimeout < TimeSpan.Zero)
            throw new InvalidOperationException("horizontalScaling:drainTimeoutSeconds cannot be negative.");
    }
}

public interface IGameServerRoutingStore
{
    public Task<bool> TryAcquireNodeLeaseAsync(GameServerNodeDescriptor descriptor, TimeSpan leaseLifetime);
    public Task<bool> TryHeartbeatNodeAsync(GameServerNodeDescriptor descriptor, TimeSpan leaseLifetime);
    public Task<bool> TryBeginDrainAsync(GameServerNodeDescriptor descriptor, TimeSpan leaseLifetime);
    public Task ReleaseNodeLeaseAsync(GameServerNodeIdentity identity);
    public Task<IReadOnlyList<GameServerNodeDescriptor>> DiscoverHealthyNodesAsync(TimeSpan maximumAge);
    public Task<GameServerMatchOwner?> TryReserveMatchAsync(
        long matchingId,
        GameServerNodeDescriptor node,
        TimeSpan ownerLifetime);
    public Task<bool> RenewMatchOwnerAsync(GameServerMatchOwner owner, TimeSpan ownerLifetime);
    public Task<bool> ClampMatchOwnerLifetimeAsync(
        GameServerMatchOwner owner,
        TimeSpan maximumRemainingLifetime);
    public Task<bool> ReleaseMatchOwnerAsync(GameServerMatchOwner owner);
    public Task<bool> IsMatchOwnerActiveAsync(GameServerMatchOwner owner);
    public Task<int> GetOwnedMatchCountAsync(GameServerNodeIdentity identity);
}

public static class GameServerRoutingKeys
{
    // One Redis hash tag keeps every fencing script in one slot when Redis Cluster is introduced.
    private const string Prefix = "{game-server-routing}:";

    public static string NodeLease(string nodeId) => $"{Prefix}node:{nodeId}:lease";
    public static string NodeAccepting(string nodeId) => $"{Prefix}node:{nodeId}:accepting";
    public static string NodeDescriptor(string nodeId) => $"{Prefix}node:{nodeId}:descriptor";
    public static string NodeSlots(string nodeId, string generation) =>
        $"{Prefix}node:{nodeId}:{generation}:slots";
    public static string MatchOwner(long matchingId) => $"{Prefix}match:{matchingId}:owner";
    public static string OwnedHandoffTicket(string ticketHash) => $"{Prefix}handoff-ticket:{ticketHash}";
    public static string OwnedHandoffConsumeReceipt(string ticketHash, string consumeNonce) =>
        $"{Prefix}handoff-ticket:{ticketHash}:consume:{consumeNonce}";
    public static string NodeHeartbeatIndex => $"{Prefix}nodes:heartbeat";
    public static string OwnerFence => $"{Prefix}owner-fence";

    public static string FormatOwnerToken(string nodeId, string generation, long fence) =>
        $"{nodeId}|{generation}|{fence}";
}
