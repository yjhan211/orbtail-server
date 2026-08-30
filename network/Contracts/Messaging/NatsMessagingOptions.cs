namespace network.contracts.messaging;

public sealed record NatsDurableStreamOptions
{
    public required string Name { get; init; }
    public required IReadOnlyCollection<string> Subjects { get; init; }
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(7);
    public long MaxBytes { get; init; } = 256L * 1024 * 1024;
    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromMinutes(10);
    public int Replicas { get; init; } = 1;
    public string? Description { get; init; }
}

public sealed record NatsDurableConsumerOptions
{
    public required string StreamName { get; init; }
    public required string Subject { get; init; }
    public required string DurableName { get; init; }
    public required string QueueGroup { get; init; }
    public required string DeliverSubject { get; init; }
    public TimeSpan AckWait { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxDeliver { get; init; } = 10;
    public int MaxAckPending { get; init; } = 256;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan AckConfirmationTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public bool DeliverNewMessagesOnly { get; init; }
}

public sealed record NatsDurableMessage(
    string Subject,
    byte[] Data,
    string? MessageId,
    ulong StreamSequence,
    ulong DeliveryAttempt);

public enum NatsDurableMessageDisposition
{
    Ack,
    Retry,
    Terminate
}

public readonly record struct NatsDurablePublishAck(
    string Stream,
    ulong Sequence,
    bool Duplicate);
