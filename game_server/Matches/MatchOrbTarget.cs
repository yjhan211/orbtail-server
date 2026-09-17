using game_server.matches.monsters;
using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     오브 공격 범위 안의 플레이어와 몬스터를 조회한다.
///     같은 구역의 유효 대상을 검사하고 공격 소유자는 제외하며, 대상의 몸통 반경을 포함한다.
/// </summary>
internal static class MatchOrbTarget
{
    public static (List<Monster> Monsters, List<Player> Players) CollectTargetsInRadius(MatchRuntime runtime, long ownerId, AreaType area, Vector3f center, float radius)
    {
        float monsterRadius = radius + GroundGeometry.MonsterRadius;
        var monsters = new List<Monster>();
        foreach (var monster in runtime.Monsters.GetCombatTargets())
        {
            if (GameMapData.GetCurrentArea(monster.Info.ObjectInfo.MapId, monster.Info.ObjectInfo.Cell) == area && GroundGeometry.IsWithinGroundRadius(center, monster.Position, monsterRadius))
            {
                monsters.Add(monster);
            }
        }

        float playerRadius = radius + GroundGeometry.PlayerRadius;
        var players = new List<Player>();
        foreach (var participant in runtime.GetAlivePlayers())
        {
            if (participant.PlayerId == ownerId || participant.Position == null || GameMapData.GetCurrentArea(participant.GameInfo.ObjectInfo.MapId, participant.GameInfo.ObjectInfo.Cell) != area)
            {
                continue;
            }
            if (GroundGeometry.IsWithinGroundRadius(center, participant.Position, playerRadius))
            {
                players.Add(participant);
            }
        }
        return (monsters, players);
    }

    public static (List<Monster> Monsters, List<Player> Players) CollectTargetsOnSunLine(MatchRuntime runtime, PendingSunAttack attack, float halfWidth, float lastFront, float front)
    {
        var monsters = new List<Monster>();
        var players = new List<Player>();

        var origin = attack.Origin;
        var end = attack.End;
        float length = GroundGeometry.GroundDistance(origin, end);
        if (length <= 0f)
        {
            return (monsters, players);
        }

        float originGroundY = origin.Y * GroundGeometry.GroundYScale; // 원점의 Y를 바닥면 좌표로 바꾼 값
        float unitX = (end.X - origin.X) / length; // 원점에서 끝점을 향하는 방향벡터
        float unitY = (end.Y * GroundGeometry.GroundYScale - originGroundY) / length; // 원점에서 끝점을 향하는 방향벡터

        foreach (var monster in runtime.Monsters.GetCombatTargets())
        {
            if (GameMapData.GetCurrentArea(monster.Info.ObjectInfo.MapId, monster.Info.ObjectInfo.Cell) != attack.Area || attack.HitMonsters.Contains(monster.MonsterId))
            {
                continue;
            }
            if (!GroundGeometry.TryGetNearestBodyAlongOnLine(origin.X, originGroundY, unitX, unitY, length, halfWidth + GroundGeometry.MonsterRadius, monster.Position, GroundGeometry.MonsterBodyHeight, out float along))
            {
                continue;
            }
            if (along > lastFront)
            {
                // 지난 틱까지 지나간 자리
                continue;
            }
            if (along <= front + GroundGeometry.MonsterRadius)
            {
                // 이번 틱의 앞머리가 대상에 닿음
                monsters.Add(monster);
            }
        }

        foreach (var participant in runtime.GetAlivePlayers())
        {
            if (participant.PlayerId == attack.OwnerId || participant.Position == null || attack.HitVictims.Contains(participant.PlayerId))
            {
                continue;
            }
            if (GameMapData.GetCurrentArea(participant.GameInfo.ObjectInfo.MapId, participant.GameInfo.ObjectInfo.Cell) != attack.Area)
            {
                continue;
            }
            if (!GroundGeometry.TryGetNearestBodyAlongOnLine(origin.X, originGroundY, unitX, unitY, length, halfWidth + GroundGeometry.PlayerRadius, participant.Position, GroundGeometry.PlayerBodyHeight, out float along))
            {
                continue;
            }
            if (along > lastFront && along <= front + GroundGeometry.PlayerRadius)
            {
                players.Add(participant);
            }
        }

        return (monsters, players);
    }
}
