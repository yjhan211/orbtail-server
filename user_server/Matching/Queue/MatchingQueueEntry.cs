using MessagePack;

namespace user_server.matching.queue;

/// <summary>
///     Redis 상세 데이터에서 읽은 매칭 요청 하나를 나타낸다.
///     대기열의 예약·삭제에는 RequestId를 사용하며 원본 바이트는 보관하지 않는다.
///     봇도 같은 타입으로 참가자 목록에 포함하지만, Redis 대기열에는 저장하지 않는다.
/// </summary>
internal sealed class MatchingQueueEntry
{
    private MatchingQueueEntry(MatchingQueueData data)
    {
        Data = data;
    }

    public MatchingQueueData Data { get; }
    public long PlayerId => Data.PlayerId;
    public DateTime RequestTime => Data.RequestTime;
    public string RequestId => Data.RequestId;
    public bool IsHuman => Data.PlayerId > 0;
    public bool IsBot => Data.PlayerId < 0;

    public static MatchingQueueEntry Parse(byte[] raw)
    {
        var data = MessagePackSerializer.Deserialize<MatchingQueueData>(raw)
            ?? throw new MessagePackSerializationException("Matching request details cannot be null.");
        return new MatchingQueueEntry(data);
    }

    public static MatchingQueueEntry FromData(MatchingQueueData data)
    {
        return new MatchingQueueEntry(data);
    }

    // 음수 PlayerId 봇 entry. 큐에는 존재하지 않고 명단 조립에만 쓴다.
    public static MatchingQueueEntry CreateBot(long botId)
    {
        return FromData(new MatchingQueueData
        {
            PlayerId = botId,
            RequestTime = DateTime.UtcNow
        });
    }
}
