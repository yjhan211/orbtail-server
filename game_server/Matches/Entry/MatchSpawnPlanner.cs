using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.entry;

/// <summary>
///     매치 스폰 결정 — Game Server 권위. matchingId 시드로 결정적이라 어느 세션이 먼저 계산해도 같은 배정이 나온다.
/// </summary>
internal static class MatchSpawnPlanner
{
    public static IReadOnlyDictionary<long, Cell> Plan(
        long matchingId, IEnumerable<long> playerIds)
    {
        var ids = playerIds.Distinct().ToList();
        return MatchSpawnData.CreatePhaseRoomAssignments(matchingId, ids);
    }
}
