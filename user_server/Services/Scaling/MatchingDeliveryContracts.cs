using MessagePack;

namespace user_server.services.scaling;

public enum MatchingDeliveryKind
{
    MatchingSucceeded = 0,
    MatchingFailed = 1,
    ClearMatchingAssignment = 2,
    DisconnectSupersededSession = 3,
    MatchingAdmissionFailed = 4
}

public enum MatchingDeliveryStatus
{
    Accepted = 0,
    StaleOwner = 1,
    SessionUnavailable = 2,
    InvalidRequest = 3,
    ShuttingDown = 4,
    RetryableFailure = 5
}

[MessagePackObject]
public sealed class MatchingDeliveryRequest
{
    [Key(0)] public string DeliveryId { get; set; } = string.Empty;
    [Key(1)] public MatchingDeliveryKind Kind { get; set; }
    [Key(2)] public long PlayerId { get; set; }
    [Key(3)] public long MatchingId { get; set; }
    [Key(4)] public string RequestId { get; set; } = string.Empty;
    [Key(5)] public string OwnerNodeId { get; set; } = string.Empty;
    [Key(6)] public string OwnerNodeGeneration { get; set; } = string.Empty;
    [Key(7)] public string OwnerSessionId { get; set; } = string.Empty;
    [Key(8)] public long OwnerSessionGeneration { get; set; }
    [Key(9)] public int ProtocolId { get; set; }
    [Key(10)] public byte[] Payload { get; set; } = Array.Empty<byte>();

    [IgnoreMember]
    public bool HasValidRoute =>
        !string.IsNullOrWhiteSpace(DeliveryId) &&
        PlayerId > 0 &&
        Enum.IsDefined(Kind) &&
        (Kind == MatchingDeliveryKind.DisconnectSupersededSession
            ? MatchingId >= 0
            : MatchingId > 0) &&
        UserServerClusterOptions.IsSafeTokenComponent(RequestId) &&
        UserServerClusterOptions.IsSafeNodeId(OwnerNodeId) &&
        UserServerClusterOptions.IsSafeTokenComponent(OwnerNodeGeneration) &&
        UserServerClusterOptions.IsSafeTokenComponent(OwnerSessionId) &&
        OwnerSessionGeneration > 0 &&
        Payload != null;
}

[MessagePackObject]
public sealed class MatchingDeliveryResponse
{
    [Key(0)] public string DeliveryId { get; set; } = string.Empty;
    [Key(1)] public MatchingDeliveryStatus Status { get; set; }

    public static MatchingDeliveryResponse Create(string? deliveryId, MatchingDeliveryStatus status)
    {
        return new MatchingDeliveryResponse
        {
            DeliveryId = deliveryId ?? string.Empty,
            Status = status
        };
    }
}

public interface IMatchingDeliveryRouter : IAsyncDisposable
{
    public UserServerProcessIdentity Identity { get; }
    public string Subject { get; }

    public void Start(Func<MatchingDeliveryRequest, CancellationToken, Task<MatchingDeliveryResponse>> handler);

    public Task<MatchingDeliveryResponse> DeliverAsync(
        MatchingDeliveryRequest request,
        CancellationToken cancellationToken = default);

    public Task StopAsync(CancellationToken cancellationToken = default);
}

public static class MatchingDeliverySubjects
{
    private const string Prefix = "user.matching.delivery";

    public static string For(UserServerProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.IsValid)
            throw new ArgumentException("UserServer process identity is invalid.", nameof(identity));
        return For(identity.NodeId, identity.Generation);
    }

    public static string For(string nodeId, string nodeGeneration)
    {
        if (!UserServerClusterOptions.IsSafeNodeId(nodeId))
            throw new ArgumentException("Node id is not safe for a NATS subject.", nameof(nodeId));
        if (!UserServerClusterOptions.IsSafeTokenComponent(nodeGeneration))
            throw new ArgumentException("Node generation is not safe for a NATS subject.", nameof(nodeGeneration));
        return $"{Prefix}.{nodeId}.{nodeGeneration}";
    }
}
