using MessagePack;

namespace user_server.matching.queue;

/// <summary>
///     매칭 큐 entry 하나를 pass 진입 시 한 번만 역직렬화해 pass 전체에서 재사용하는 typed 모델.
///     <see cref="Raw" />는 Redis sorted set에서 같은 entry를 제거할 때 쓰는 원본 바이트다
///     (봇 entry는 큐에 들어가지 않으므로 제거 대상이 아니다).
/// </summary>
internal sealed class MatchingQueueEntry
{
    private MatchingQueueEntry(byte[] raw, MatchingQueueData data)
    {
        Raw = raw;
        Data = data;
    }

    public byte[] Raw { get; }
    public MatchingQueueData Data { get; }
    public long PlayerId => Data.PlayerId;
    public DateTime RequestTime => Data.RequestTime;
    public string RequestId => Data.RequestId;
    public bool IsHuman => Data.PlayerId > 0;
    public bool IsBot => Data.PlayerId < 0;

    /// <summary>
    ///     큐에서 읽은 원본 바이트를 역직렬화한다. 손상된 entry는 MessagePack 예외를 그대로 던진다.
    /// </summary>
    public static MatchingQueueEntry Parse(byte[] raw)
    {
        return new MatchingQueueEntry(raw, MessagePackSerializer.Deserialize<MatchingQueueData>(raw));
    }

    public static MatchingQueueEntry FromData(MatchingQueueData data)
    {
        return new MatchingQueueEntry(MessagePackSerializer.Serialize(data), data);
    }

    /// <summary>
    ///     음수 PlayerId 봇 entry. 큐에는 존재하지 않고 로스터 조립에만 쓴다.
    /// </summary>
    public static MatchingQueueEntry CreateBot(long botId)
    {
        return FromData(new MatchingQueueData
        {
            PlayerId = botId,
            RequestTime = DateTime.UtcNow
        });
    }
}
