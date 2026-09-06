using System.Text.RegularExpressions;

namespace user_server.matching.queue;

/// <summary>
///     매칭 요청 ID처럼 Redis 키·로그·패킷에 그대로 들어가는 토큰 조각의 허용 문자를 검사한다.
/// </summary>
internal static class MatchingRequestTokens
{
    private static readonly Regex SafeTokenPattern = new(
        "^[A-Za-z0-9_-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool IsSafeTokenComponent(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && SafeTokenPattern.IsMatch(value);
    }
}
