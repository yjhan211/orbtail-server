using game_server.players.bots;
using game_server.matches.monsters;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
/// 전투 전에 봇·몬스터의 이동 단계를 실행한다. 카운트다운 중에는 몬스터만 이동한다.
/// 행동 서비스가 개체별 이동을 수행하며 공통 경로 진행과 문 통과 검사는 Move를 사용한다.
/// 개체별 경로는 MovementState가 소유하고 호출자는 매치 잠금을 보유한다.
/// </summary>
internal class MatchMovementService(
    BotBehaviorService botBehavior,
    MonsterBehaviorService monsterBehavior,
    MatchMonsterSpawnService monsterSpawns)
{
    public virtual void ProcessTick(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement tick requires the match lock.");
        }
        if (runtime.IsEnded || runtime.Mode == MatchMode.SoloMapValidation)
        {
            return;
        }
        bool isGameplayActive = runtime.IsGameplayActive(nowUtc);
        if (isGameplayActive && runtime.Bots.HasBots())
        {
            botBehavior.ProcessTick(runtime, id => botBehavior.DecideMovement(runtime, id));
        }
        var participants = new List<PlayerPositionSnapshot>();
        foreach (var player in runtime.GetAlivePlayers())
        {
            if (player.Position != null)
            {
                participants.Add(new PlayerPositionSnapshot(player.PlayerId, player.CurrentArea, player.Position));
            }
        }
        if (participants.Count == 0)
        {
            return;
        }
        ProcessMonsters(runtime, participants, isGameplayActive, nowUtc);
    }

    internal void ProcessMonsters(MatchRuntime runtime, IReadOnlyList<PlayerPositionSnapshot> participants, bool isGameplayActive, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster movement tick requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            return;
        }
        var state = runtime.Monsters;
        state.Initialize(nowUtc);
        double deltaSeconds = Math.Clamp((nowUtc - state.LastTickAtUtc).TotalSeconds, 0d, 0.25d);
        state.LastTickAtUtc = nowUtc;
        if ((nowUtc - state.StartsAtUtc).TotalSeconds >= Config.SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS)
        {
            deltaSeconds *= Config.SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER;
        }
        bool preMatch = !isGameplayActive;
        monsterSpawns.ProcessSupply(runtime, participants, nowUtc, preMatch);
        foreach (var monster in state.Entities.Values)
        {
            if (!monster.Alive || nowUtc < monster.ActivatesAtUtc)
            {
                continue;
            }
            monsterBehavior.Move(runtime, monster, participants, nowUtc, deltaSeconds, preMatch);
            monsterBehavior.RescueMonsterFromBlockedCell(runtime, monster);
        }
        state.RemoveExpiredDead(nowUtc);
    }

    public static Vector3f Move(MatchRuntime runtime, MovementState movement, Vector3f position,
        float distanceBudget, bool ignoreClosedDoors = false,
        Func<Vector3f, bool>? canEnter = null)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot move after the match has ended.");
        }
        var mapId = Config.SWARM_MATCH_MAP;
        var route = movement.Waypoints;
        int index = movement.WaypointIndex;
        var current = new Vector3f(position.X, position.Y, 0f);
        while (distanceBudget > 0f && index < route.Count)
        {
            var target = route[index];
            float dx = target.X - current.X;
            float dy = target.Y - current.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance == 0f)
            {
                index++;
                continue;
            }

            var fromCell = MapCoordinateConverter.WorldToCell(mapId, current);
            var targetCell = MapCoordinateConverter.WorldToCell(mapId, target);
            var fromArea = GameMapData.GetCurrentArea(mapId, fromCell);
            var targetArea = GameMapData.GetCurrentArea(mapId, targetCell);
            var door = GameDoorData.GetDoorForTransition(fromArea, targetArea, fromCell, targetCell);
            if (door != null && !ignoreClosedDoors && !runtime.Doors.IsDoorOpen(door.DoorId))
            {
                break;
            }
            if (canEnter != null && !canEnter(target))
            {
                break;
            }
            if (!MapPathfinder.IsSegmentWalkable(mapId, current, target))
            {
                break;
            }

            float step = Math.Min(distanceBudget, distance);
            var next = Vector3f.MoveTowardsXY(current, target, step);
            if (canEnter != null && !canEnter(next))
            {
                break;
            }
            current = next;
            distanceBudget -= step;
            if (step == distance)
            {
                current = new Vector3f(target.X, target.Y, 0f);
                index++;
            }
        }
        movement.WaypointIndex = index;
        return current;
    }

}
