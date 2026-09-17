using game_server.matches;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace server_tests;

/// <summary>
///     개별 게임 규칙 테스트에서 사용할 매치 전용 객체를 만든다.
///     여러 매치의 분리와 종료를 검사할 때는 MatchRuntimeStore를 직접 사용한다.
/// </summary>
internal static class MatchTestServices
{
    public static MatchRuntime Runtime(long matchingId, ILogger logger) => TestGameSessionServices.CreateMatchRuntimeStore(logger).GetOrCreate(matchingId);
    public static MatchAreaClosureState Closures(Func<DateTime>? utcNow = null) => new(utcNow);
}
