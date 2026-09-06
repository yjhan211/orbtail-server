using game_server.services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

/// <summary>
///     개별 게임 규칙 테스트용 조립. 별도 런타임을 만들며 테스트가 요청한 매치만 준비한다.
///     실제 서버와 종료 테스트는 MatchRuntimeStore.Get을 사용해 사라진 매치를 다시 만들지 않는다.
/// </summary>
internal static class MatchTestServices
{
    private static Func<long, MatchRuntime?> CreateMatches() =>
        new MatchRuntimeStore(NullLogger.Instance).GetOrCreate;

    public static InGameInventoryManager Inventory() => new(CreateMatches());
    public static GroundItemManager GroundItems(TimeProvider? timeProvider = null) => new(CreateMatches(), timeProvider);
    public static SummonStoneManager SummonStones() => new(CreateMatches());
    public static EncounterRevealManager Encounters() => new(CreateMatches());
    public static MatchRosterManager Roster(ILogger logger) => new(CreateMatches(), logger);
    public static AreaClosureManager Closures(ILogger logger, Func<DateTime>? utcNow = null) =>
        new(CreateMatches(), logger, utcNow);
}
