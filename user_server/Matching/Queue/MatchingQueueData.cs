using MessagePack;

namespace user_server.matching.queue;

/// <summary>
///     Redis 매칭 큐(sorted set)에 저장되는 wire 타입. Key 번호는 기존 entry 호환을 위해 유지한다.
/// </summary>
[MessagePackObject]
public class MatchingQueueData
{
    [Key(0)]
    public long PlayerId { get; set; }

    [Key(1)]
    public DateTime RequestTime { get; set; }

    // Key 2는 #323에서 삭제된 write-only UserChannel, Key 3~6은 #320에서 삭제된 세션 owner 경로.
    // 큐 entry 호환을 위해 번호를 유지한다.
    [Key(7)]
    public string RequestId { get; set; } = string.Empty;
}
