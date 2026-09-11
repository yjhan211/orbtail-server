using System.Reflection;
using game_server.matches;

namespace demo_regression_tests;

internal static class TestGroundItemLanding
{
    // 획득 결과 테스트는 착지가 끝난 상태로 시작한다. 실제 시간 경계는 별도 가짜 시계로 검증한다.
    public static void Complete(MatchGroundItemState items, double ageSeconds = 1)
    {
        var spawnedAt = (Dictionary<long, DateTimeOffset>)typeof(MatchGroundItemState)
            .GetField("_spawnedAtUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(items)!;
        foreach (long id in spawnedAt.Keys.ToArray())
            spawnedAt[id] = DateTimeOffset.UtcNow.AddSeconds(-ageSeconds);
    }
}
