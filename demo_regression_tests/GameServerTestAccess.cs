using System.Reflection;
using game_server;
using game_server.services;

namespace demo_regression_tests;

// 서버의 private 구성 요소를 조립·수명 검증에서만 조회한다.
// 테스트 때문에 운영 코드의 접근 범위를 넓히지 않는다.
internal static class GameServerTestAccess
{
    internal static MatchRuntimeStore GetMatchRuntimes(this GameServer server) =>
        Read<MatchRuntimeStore>(server, "MatchRuntimes");

    internal static GameEventLogManager GetEventLogs(this GameServer server) =>
        Read<GameEventLogManager>(server, "EventLogs");

    internal static MatchEntryFailureHandler GetEntryFailureHandler(this GameServer server) =>
        Read<MatchEntryFailureHandler>(server, "EntryFailureHandler");

    internal static MatchingLifecycleService GetMatchingLifecycle(this GameServer server) =>
        Read<MatchingLifecycleService>(server, "MatchingLifecycle");

    private static T Read<T>(GameServer server, string name) where T : class
    {
        var property = typeof(GameServer).GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(server));
    }
}
