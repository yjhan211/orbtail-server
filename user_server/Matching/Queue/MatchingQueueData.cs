using MessagePack;

namespace user_server.matching.queue;

/// <summary>
///     매칭 요청 하나의 데이터. Redis 상세 저장과 매칭 처리에 함께 사용한다.
/// </summary>
[MessagePackObject]
public class MatchingQueueData
{
    [Key("playerId")] public long PlayerId { get; init; }

    [Key("requestTime")] public DateTime RequestTime { get; init; }

    [Key("requestId")] public string RequestId { get; init; } = string.Empty;

    public static MatchingQueueData Parse(byte[] raw)
    {
        return MessagePackSerializer.Deserialize<MatchingQueueData>(raw)
            ?? throw new MessagePackSerializationException("Matching request details cannot be null.");
    }
}
