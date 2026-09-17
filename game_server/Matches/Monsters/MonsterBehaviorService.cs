using game_server.players;
using network.common;
using network.common.data;

namespace game_server.matches.monsters;

/// <summary>
///     몬스터의 추격 대상을 결정한다.
///     경로 계획과 이동 실행은 MatchMoveService가 담당한다.
/// </summary>
internal sealed class MonsterBehaviorService
{
    public MovementRequest CreateMovementRequest(MatchRuntime runtime, Monster monster, IReadOnlyList<Player> participants, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster decisions require the match lock.");
        }
        if (runtime.IsEnded || !monster.Alive)
        {
            return new MovementRequest(null, 0f);
        }

        var target = SelectChaseTarget(monster, participants);
        monster.ChaseTargetPlayerId = target?.PlayerId ?? 0;
        if (target == null)
        {
            return new MovementRequest(null, 0f);
        }

        float speed = Config.SWARM_MONSTER_MOVE_SPEED;
        if (runtime.Monsters.IsInitialized && (nowUtc - runtime.Monsters.StartsAtUtc).TotalSeconds >= Config.SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS)
        {
            speed *= Config.SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER;
        }

        var destinationCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target.Position!);
        return new MovementRequest(destinationCell, destinationCell.Equals(monster.Info.ObjectInfo.Cell) ? 0f : speed);
    }

    internal static Player? SelectChaseTarget(Monster monster, IReadOnlyList<Player> participants)
    {
        Player? nearestSameAreaPlayer = null;
        Player? nearestOtherAreaPlayer = null;
        float nearestSameAreaDistanceSquared = float.MaxValue;
        float nearestOtherAreaDistanceSquared = float.MaxValue;
        var monsterInfo = monster.Info.ObjectInfo;
        var monsterArea = GameMapData.GetCurrentArea(monsterInfo.MapId, monsterInfo.Cell);
        foreach (var participant in participants)
        {
            if (participant.IsEliminated || participant.Position == null)
            {
                continue;
            }
            var participantInfo = participant.GameInfo.ObjectInfo;
            var participantArea = GameMapData.GetCurrentArea(participantInfo.MapId, participantInfo.Cell);
            if (participantArea == AreaType.None)
            {
                continue;
            }

            // XY 직선거리로 비교
            float dx = participant.Position.X - monster.Position.X;
            float dy = participant.Position.Y - monster.Position.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (participantArea == monsterArea)
            {
                if (distanceSquared < nearestSameAreaDistanceSquared)
                {
                    nearestSameAreaDistanceSquared = distanceSquared;
                    nearestSameAreaPlayer = participant;
                }
                continue;
            }
            if (distanceSquared < nearestOtherAreaDistanceSquared)
            {
                nearestOtherAreaDistanceSquared = distanceSquared;
                nearestOtherAreaPlayer = participant;
            }
        }

        return nearestSameAreaPlayer ?? nearestOtherAreaPlayer;
    }
}
