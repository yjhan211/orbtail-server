using game_server.players;
using network.common;
using network.common.data;

namespace game_server.matches.monsters;

/// <summary>
///     몬스터의 탐지·추격 대상을 결정한다.
///     경로 계획과 이동 실행은 MatchMoveService가 담당한다.
/// </summary>
internal sealed class MonsterBehaviorService
{
    public MovementRequest CreateMovementRequest(MatchRuntime runtime, Monster monster, IReadOnlyList<Player> participants, DateTime now)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster decisions require the match lock.");
        }
        if (runtime.IsEnded || !monster.Alive)
        {
            return new MovementRequest(null, 0f);
        }

        float speed = Config.SWARM_MONSTER_MOVE_SPEED;
        if (now < monster.WaveSlowUntilUtc)
        {
            speed *= OrbData.WaveSlowMoveSpeedMultiplier;
        }
        if (runtime.Monsters.IsInitialized)
        {
            double elapsedSeconds = (now - runtime.Monsters.StartsAtUtc).TotalSeconds;
            if (elapsedSeconds >= Config.SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS)
            {
                speed *= Config.SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER;
            }
        }

        if (TrySelectChaseTarget(monster, participants, out var target))
        {
            var destinationCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target.Position!);
            if (destinationCell.Equals(monster.Info.ObjectInfo.Cell))
            {
                return new MovementRequest(destinationCell, 0f);
            }
            return new MovementRequest(destinationCell, speed);
        }

        monster.ChaseTargetPlayerId = 0;
        return new MovementRequest(null, 0f);
    }

    internal bool TrySelectChaseTarget(Monster monster, IReadOnlyList<Player> participants, out Player target)
    {
        // 구역 우선순위를 적용할 수 있도록 같은 구역과 다른 구역의 후보를 따로 보관
        Player? nearestSameAreaPlayer = null;
        Player? nearestOtherAreaPlayer = null;
        float nearestOtherAreaDistanceSquared = float.MaxValue;
        float nearestSameAreaDistanceSquared = float.MaxValue;
        foreach (var participant in participants)
        {
            if (participant.GameInfo.ObjectInfo.Area == AreaType.None || participant.IsEliminated || participant.Position == null)
            {
                continue;
            }
            // XY 직선거리로 비교
            float dx = participant.Position.X - monster.Position.X;
            float dy = participant.Position.Y - monster.Position.Y;
            float distanceSquared = dx * dx + dy * dy;
            // 다른 구역 후보
            if (participant.GameInfo.ObjectInfo.Area != monster.Area)
            {
                if (distanceSquared >= nearestOtherAreaDistanceSquared)
                {
                    continue;
                }
                nearestOtherAreaDistanceSquared = distanceSquared;
                nearestOtherAreaPlayer = participant;
                continue;
            }
            // 같은 구역 후보
            if (distanceSquared >= nearestSameAreaDistanceSquared)
            {
                continue;
            }
            nearestSameAreaDistanceSquared = distanceSquared;
            nearestSameAreaPlayer = participant;
        }

        // 다른 구역의 후보가 더 가깝더라도 같은 구역에 유효한 후보가 있으면 먼저 선택한다.
        target = (nearestSameAreaPlayer ?? nearestOtherAreaPlayer);
        if (target == null)
        {
            // 유효한 후보가 없다. 호출부에서 추적 ID를 지우고 정지 요청을 만든다.
            return false;
        }

        monster.ChaseTargetPlayerId = target.PlayerId;
        return true;
    }
}
