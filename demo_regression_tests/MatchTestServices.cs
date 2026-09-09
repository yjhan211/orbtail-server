using game_server.items;
using game_server.field;
using game_server.matches.results;
using game_server.matches;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

/// <summary>
///     개별 게임 규칙 테스트에서 사용할 매치 전용 객체를 만든다.
///     여러 매치의 분리와 종료를 검사할 때는 MatchRuntimeStore를 직접 사용한다.
/// </summary>
internal static class MatchTestServices
{
    public static InGameInventoryManager Inventory(long matchingId = 1) => new(matchingId, NullLogger.Instance);
    public static GroundItemManager GroundItems(long matchingId = 1, TimeProvider? timeProvider = null) => new(matchingId, timeProvider);
    public static SummonStoneManager SummonStones(long matchingId = 1) => new(matchingId);
    public static EncounterRevealManager Encounters() => new();
    public static MatchRoster Roster(long matchingId, ILogger logger) => new(matchingId, logger);
    public static AreaClosureManager Closures(long matchingId, ILogger logger, Func<DateTime>? utcNow = null) =>
        new(matchingId, logger, utcNow);
}
