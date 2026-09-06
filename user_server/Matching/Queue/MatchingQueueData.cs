using MessagePack;
using System.Text.RegularExpressions;

namespace user_server.matching.queue;

/// <summary>
///     Redis 매칭 대기열에 저장하는 플레이어의 매칭 요청 데이터.
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
}
