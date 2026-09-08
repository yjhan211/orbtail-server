using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.entry;

/// <summary>
///     매치 스폰 결정 — Game Server 권위. matchingId 시드로 결정적이라 어느 세션이 먼저 계산해도 같은 배정이 나온다.
///     crossfireSandbox이면 전원 운동장 스폰(교차사격 실험용 — 정식 흐름은 잠긴 시작방에서 운동장까지 100초가 걸린다).
/// </summary>
internal static class MatchSpawnPlanner
{
    public static IReadOnlyDictionary<long, Cell> Plan(
        long matchingId, MapId mapId, IEnumerable<long> playerIds, bool crossfireSandbox = false)
    {
        var ids = playerIds.Distinct().ToList();
        if (!crossfireSandbox)
            return MatchSpawnData.CreatePhaseRoomAssignments(matchingId, ids);

        Cell ground = GameMapData.GetAreaSpawnCell(mapId, Config.SWARM_MATCH_GROUND_AREA);
        return ids.ToDictionary(id => id, _ => Cell.Clone(ground));
    }
}
