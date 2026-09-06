using MessagePack;
using System.Text.RegularExpressions;

namespace user_server.matching.queue;

/// <summary>
///     매칭 요청 하나의 데이터. Redis 상세 저장과 매칭 처리에 함께 사용한다.
///     봇도 같은 타입으로 명단에 포함하지만 Redis 대기열에는 저장하지 않는다.
/// </summary>
[MessagePackObject]
public class MatchingQueueData
{
    private static readonly Regex RequestIdPattern = new(
        "^[A-Za-z0-9_-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValidRequestId(string? requestId)
    {
        return !string.IsNullOrWhiteSpace(requestId) && RequestIdPattern.IsMatch(requestId);
    }

    [Key("playerId")] public long PlayerId { get; init; }

    [Key("requestTime")] public DateTime RequestTime { get; init; }

    [Key("requestId")] public string RequestId { get; init; } = string.Empty;

    [IgnoreMember] public bool IsHuman => PlayerId > 0;
    [IgnoreMember] public bool IsBot => PlayerId < 0;

    public static MatchingQueueData Parse(byte[] raw)
    {
        return MessagePackSerializer.Deserialize<MatchingQueueData>(raw)
            ?? throw new MessagePackSerializationException("Matching request details cannot be null.");
    }

    public static MatchingQueueData CreateBot(long botId)
    {
        return new MatchingQueueData
        {
            PlayerId = botId,
            RequestTime = DateTime.UtcNow
        };
    }
}
