using System.Reflection;
using game_server.matches;

namespace server_tests;

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

    // 쌓인 획득 후보를 꺼내고 비운다. 서비스가 틱에서 하는 소진과 같은 동작이다.
    public static game_server.players.Player.ReachableItem[] TakeReachableItems(game_server.players.Player player)
    {
        var items = player.ReachableItems.Values.ToArray();
        player.ReachableItems.Clear();
        return items;
    }
}
