using System.Collections.Immutable;
using game_server.players;
using network.common;
using network.common.data;

namespace game_server.matches;

/// <summary>
///     플레이어별 오브 표시 정보(구역·선두 오브·본체 체력·꼬리 오브 목록).
///     각 세션이 이전에 보낸 상태와 비교해 변경된 정보만 전송하는 데 사용한다.
/// </summary>
internal sealed record MatchOrbVisual(long ActorPlayerId, AreaType Area, int WeaponItemId, int BodyHealth, ImmutableArray<int> OrbItemIds)
{
    public bool HasSameState(MatchOrbVisual other) =>
        Area == other.Area && WeaponItemId == other.WeaponItemId && BodyHealth == other.BodyHealth && OrbItemIds.SequenceEqual(other.OrbItemIds);

    public static List<MatchOrbVisual> Build(MatchRuntime runtime, IReadOnlyList<Player> players)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb visual build requires the match lock.");
        }

        var visuals = new List<MatchOrbVisual>(players.Count);
        foreach (var player in players)
        {
            if (player.IsEliminated || player.Position == null || GameMapData.GetCurrentArea(player.GameInfo.ObjectInfo.MapId, player.GameInfo.ObjectInfo.Cell) == AreaType.None)
            {
                continue;
            }
            var cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, player.Position);
            if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell))
            {
                continue;
            }

            var orbs = runtime.GetOrbs(player.PlayerId).GetOrderedOrbs();
            var orbItemIds = ImmutableArray.CreateBuilder<int>(orbs.Count);
            foreach (var orb in orbs)
            {
                orbItemIds.Add(orb.ItemId);
            }
            int leadOrbItemId = orbs.Count > 0 ? orbs[0].ItemId : 0;
            visuals.Add(new MatchOrbVisual(player.PlayerId, GameMapData.GetCurrentArea(player.GameInfo.ObjectInfo.MapId, player.GameInfo.ObjectInfo.Cell), leadOrbItemId, player.Health, orbItemIds.MoveToImmutable()));
        }
        return visuals;
    }
}
