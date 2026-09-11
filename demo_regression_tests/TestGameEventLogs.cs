using System.Collections.Concurrent;
using game_server.matches;
using game_server.matches.logging;

namespace demo_regression_tests;

// 로그 계산 단위 테스트에서만 매치 상태를 대신 제공한다.
internal static class TestGameEventLogs
{
    public static GameEventLogManager Create()
    {
        var states = new ConcurrentDictionary<long, EventLogState>();
        return new GameEventLogManager(id => states.GetOrAdd(id, _ => new EventLogState()));
    }
}
