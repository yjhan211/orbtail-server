using System.Text.RegularExpressions;

namespace user_server.services.scaling;

public sealed class UserServerClusterOptions
{
    private static readonly Regex SafeNodeIdPattern = new(
        "^[A-Za-z0-9_-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public bool Enabled { get; init; }
    public string NodeId { get; init; } = string.Empty;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan NodeLeaseLifetime { get; init; } = TimeSpan.FromSeconds(12);
    public TimeSpan SessionOwnerLifetime { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MatchingLeaderHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan MatchingLeaderLeaseLifetime { get; init; } = TimeSpan.FromSeconds(12);
    public TimeSpan DeliveryRequestTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan DeliveryRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public int DeliveryMaxAttempts { get; init; } = 3;

    public void Validate()
    {
        if (!Enabled)
            return;

        if (!IsSafeNodeId(NodeId))
        {
            throw new InvalidOperationException(
                "userServerScaling:nodeId is required and must contain only letters, numbers, '_' or '-' " +
                "(maximum 64 characters) when scaling is enabled.");
        }

        ValidateLease(
            HeartbeatInterval,
            NodeLeaseLifetime,
            "userServerScaling:heartbeatSeconds",
            "userServerScaling:nodeLeaseSeconds");
        ValidateLease(
            HeartbeatInterval,
            SessionOwnerLifetime,
            "userServerScaling:heartbeatSeconds",
            "userServerScaling:sessionOwnerLeaseSeconds");
        ValidateLease(
            MatchingLeaderHeartbeatInterval,
            MatchingLeaderLeaseLifetime,
            "userServerScaling:matchingLeaderHeartbeatSeconds",
            "userServerScaling:matchingLeaderLeaseSeconds");

        if (DeliveryRequestTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "userServerScaling:deliveryRequestTimeoutMilliseconds must be greater than zero.");
        if (DeliveryRetryDelay < TimeSpan.Zero)
            throw new InvalidOperationException(
                "userServerScaling:deliveryRetryDelayMilliseconds cannot be negative.");
        if (DeliveryMaxAttempts is <= 0 or > 10)
            throw new InvalidOperationException(
                "userServerScaling:deliveryMaxAttempts must be between 1 and 10.");
    }

    internal static bool IsSafeNodeId(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && SafeNodeIdPattern.IsMatch(value);
    }

    internal static bool IsSafeTokenComponent(string? value)
    {
        return IsSafeNodeId(value);
    }

    private static void ValidateLease(
        TimeSpan heartbeat,
        TimeSpan lifetime,
        string heartbeatName,
        string lifetimeName)
    {
        if (heartbeat <= TimeSpan.Zero)
            throw new InvalidOperationException($"{heartbeatName} must be greater than zero.");
        if (lifetime <= heartbeat * 2)
            throw new InvalidOperationException($"{lifetimeName} must exceed twice {heartbeatName}.");
    }
}

public sealed record UserServerProcessIdentity(string NodeId, string Generation)
{
    private const string SingleNodeId = "single";

    public bool IsValid =>
        UserServerClusterOptions.IsSafeNodeId(NodeId) &&
        UserServerClusterOptions.IsSafeTokenComponent(Generation);

    public string Token => $"{NodeId}|{Generation}";

    public static UserServerProcessIdentity Create(UserServerClusterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        string nodeId = options.Enabled ? options.NodeId : SingleNodeId;
        return new UserServerProcessIdentity(nodeId, Guid.NewGuid().ToString("N"));
    }
}
