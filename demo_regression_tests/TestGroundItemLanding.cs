using game_server.items;
using System.Reflection;

namespace demo_regression_tests;

internal static class TestGroundItemLanding
{
    // 획득 결과 테스트는 착지가 끝난 상태로 시작한다. 실제 시간 경계는 별도 가짜 시계로 검증한다.
    public static void Complete(GroundItemManager manager, double ageSeconds = 1)
    {
        var state = (GroundItemManager.MatchingGroundItemState)typeof(GroundItemManager)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        lock (state.SyncRoot)
            foreach (long id in state.SpawnedAtUtc.Keys.ToArray())
                state.SpawnedAtUtc[id] = DateTimeOffset.UtcNow.AddSeconds(-ageSeconds);
    }
}
