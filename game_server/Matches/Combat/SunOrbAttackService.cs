using game_server.matches.logging;
using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.matches.combat;

internal sealed class SunOrbAttackService(
    PlayerHealthService healthService,
    MatchCombatDamageService combatDamage,
    GameEventLogManager eventLogs)
{
    private const float SwarmGroundYScale = SwarmCombatGeometry.GroundYScale;
    private const float SwarmCrossfirePlayerRadius = SwarmCombatGeometry.PlayerRadius;
    private const float SwarmCrossfirePlayerBodyHeight = 0.9f;
    private const float SwarmCrossfireMonsterRadius = SwarmCombatGeometry.MonsterRadius;
    private const float SwarmCrossfireMonsterBodyHeight = 0.6f;
    private static readonly bool SwarmCrossfireEnabled = true;
    private static long _lastEventId;

    public static bool IsSwarmCrossfireWeapon(int weaponItemId) => IsSwarmCrossfireSun(weaponItemId);
    public static bool IsSwarmCrossfireSun(int weaponItemId) => SwarmCrossfireEnabled && OrbData.TryGetColorAndTier(weaponItemId, out var color, out _) && color == OrbColor.Red;
    public static long AllocateEventId() => Interlocked.Increment(ref _lastEventId);

    public static int CountTelegraphing(IReadOnlyList<SwarmCrossfireShape> shapes, long ownerId, DateTime nowUtc)
    {
        int count = 0;
        foreach (var shape in shapes)
        {
            if (shape.OwnerId == ownerId && nowUtc < shape.ArmedAtUtc)
            {
                count++;
            }
        }
        return count;
    }
    public static IReadOnlyList<SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat> BuildDodgeSnapshot(IReadOnlyList<SwarmCrossfireShape> shapes)
    {
        var threats = new List<SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat>(shapes.Count);
        foreach (var shape in shapes)
        {
            float axisX = shape.End.X - shape.Origin.X;
            float axisY = (shape.End.Y - shape.Origin.Y) * SwarmGroundYScale;
            float length = MathF.Sqrt(axisX * axisX + axisY * axisY);
            if (length < 0.01f)
            {
                continue;
            }

            threats.Add(new SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat(
                shape.Area,
                shape.OwnerId,
                shape.Origin.X,
                shape.Origin.Y,
                axisX / length,
                axisY / length,
                shape.GroundLength,
                shape.HalfWidth,
                shape.SweepSpeed,
                shape.ArmedAtUtc,
                shape.ExpiresAtUtc));
        }
        return threats.ToArray();
    }

    public HashSet<(long OwnerId, long CombatTargetId)> CollectSwarmCrossfireAnchoredTargets(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Sun orb attacks require the match lock.");
        }
        var anchored = new HashSet<(long, long)>();
        foreach (var shape in runtime.SunOrbAttacks.Shapes)
        {
            anchored.Add((shape.OwnerId, shape.AnchorCombatTargetId));
        }
        return anchored;
    }

    public HashSet<long> CollectSwarmCrossfireCappedOwners(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Sun orb attacks require the match lock.");
        }
        var telegraphingByOwner = new Dictionary<long, int>();
        foreach (var shape in runtime.SunOrbAttacks.Shapes)
        {
            if (nowUtc >= shape.ArmedAtUtc)
            {
                continue;
            }
            telegraphingByOwner[shape.OwnerId] = telegraphingByOwner.GetValueOrDefault(shape.OwnerId) + 1;
        }

        var capped = new HashSet<long>();
        foreach ((long ownerId, int count) in telegraphingByOwner)
        {
            if (count >= Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER)
            {
                capped.Add(ownerId);
            }
        }
        return capped;
    }

    public void ProcessSwarmCrossfires(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Sun orb attacks require the match lock.");
        }

        if (runtime.IsEnded)
        {
            return;
        }
        long matchingId = runtime.MatchingId;
        var players = runtime.GetAlivePlayers();
        var allSessions = runtime.GetSessions().Where(session => !session.IsGameEnded).ToList();
        if (!SwarmCrossfireEnabled)
        {
            return;
        }

        var shapes = runtime.SunOrbAttacks.Shapes;
        IReadOnlyList<SwarmArenaCombatTarget>? monsters = null;
        int shapesBefore = shapes.Count;
        for (int index = shapes.Count - 1; index >= 0; index--)
        {
            var shape = shapes[index];
            if (nowUtc < shape.ArmedAtUtc)
            {
                continue;
            }

            float sweepEnd = shape.GroundLength + shape.HalfWidth;
            float front = nowUtc >= shape.ExpiresAtUtc ? sweepEnd : -shape.HalfWidth + (float)(nowUtc - shape.ArmedAtUtc).TotalSeconds * shape.SweepSpeed;
            front = MathF.Min(front, sweepEnd);
            float lastFront = shape.LastFront;
            shape.LastFront = front;
            monsters ??= runtime.Monsters.GetCombatTargets();

            foreach (var monster in monsters)
            {
                if (monster.Area != shape.Area || shape.HitMonsters.Contains(monster.CombatTargetId))
                {
                    continue;
                }

                if (!IsSwarmCrossfireSweptBody(shape, monster.Position, lastFront, front, SwarmCrossfireMonsterRadius, SwarmCrossfireMonsterBodyHeight))
                {
                    continue;
                }

                shape.HitMonsters.Add(monster.CombatTargetId);
                eventLogs.LogCrossfireHit(matchingId, monster.CombatTargetId, nowUtc);
                runtime.Monsters.RecordMonsterAttackEvent(monster.CombatTargetId);
                int monsterDamage = combatDamage.RollSwarmCriticalDamage(runtime, shape.Damage, out bool critical);
                combatDamage.ApplySwarmMonsterHitNow(runtime, monster.CombatTargetId, monster.MonsterId, shape.OwnerId, shape.WeaponItemId, shape.Area, monsterDamage, critical, allSessions);
            }

            foreach (var participant in players)
            {
                if (participant.IsEliminated || participant.Position == null)
                {
                    continue;
                }

                if (participant.PlayerId == shape.OwnerId || participant.CurrentArea != shape.Area || shape.HitVictims.Contains(participant.PlayerId))
                {
                    continue;
                }

                if (!IsSwarmCrossfireSweptBody(shape, participant.Position!, lastFront, front, SwarmCrossfirePlayerRadius, SwarmCrossfirePlayerBodyHeight))
                {
                    continue;
                }

                shape.HitVictims.Add(participant.PlayerId);
                combatDamage.ApplySwarmShock(runtime, healthService, shape.OwnerId, shape.WeaponItemId, shape.Area, participant.PlayerId, $"ORB_CROSSFIRE_HIT event={shape.EventId} shape=pierce anchor={shape.AnchorMonsterId}", players);
                if (runtime.IsEnded)
                {
                    return;
                }
                // 화상 부여·갱신 — 첫 틱은 1초 뒤(직격과 같은 프레임에 겹치지 않게). 재피격은 지속·주기를 새로 잡는다.
                participant.SunBurn = new Player.SunBurnState(shape.OwnerId, shape.WeaponItemId, shape.Area, nowUtc.AddSeconds(Config.SWARM_SUN_BURN_SECONDS), nowUtc.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS));
                if (participant.Session is { PlayerId: not null } victimSession && shape.OwnerId != 0)
                {
                    using var burnPacket = PacketMaker.G_TO_C_STATUS_EFFECT(new()
                    {
                        SourcePlayerId = shape.OwnerId,
                        TargetPlayerId = participant.PlayerId,
                        AreaType = shape.Area,
                        Effect = CombatStatusEffectKind.SunBurn,
                        DurationMs = (int)(Config.SWARM_SUN_BURN_SECONDS * 1000f)
                    });
                    victimSession.TrySend(burnPacket);
                }
            }

            if (front < sweepEnd)
                continue;

            shapes.RemoveAt(index);
            if (shape.DetonateAtWall)
            {
                // 벽 폭발은 시각 연출만이다. 잘린 직선의 끝(첫 이동 불가 셀 앞)에서 터지고, 같은 구역 전원에게 폭발 지점·표시 반경만 보낸다.
                float axisOriginX = shape.Origin.X;
                float axisOriginY = shape.Origin.Y * SwarmGroundYScale;
                float axisLength = MathF.Max(shape.GroundLength, 1e-4f);
                float axisUnitX = (shape.End.X - axisOriginX) / axisLength;
                float axisUnitY = (shape.End.Y * SwarmGroundYScale - axisOriginY) / axisLength;
                var detonation = new Vector3f(axisOriginX + axisUnitX * shape.GroundLength, (axisOriginY + axisUnitY * shape.GroundLength) / SwarmGroundYScale, 0f);
                using var detonationPacket = Packet.Create((int)Protocol.G_TO_C_SUN_ORB_ATTACK);
                detonationPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUN_ORB_ATTACK
                {
                    EventId = shape.EventId,
                    OwnerPlayerId = shape.OwnerId,
                    WeaponItemId = shape.WeaponItemId,
                    Shape = Config.SWARM_CROSSFIRE_SHAPE_DETONATE,
                    OriginX = detonation.X,
                    OriginY = detonation.Y,
                    EndX = detonation.X,
                    EndY = detonation.Y,
                    Width = shape.BlastRadius,
                    TelegraphSeconds = 0f,
                    ActiveSeconds = 0f,
                    AnchorMonsterId = shape.AnchorMonsterId,
                    OwnerOrbOrdinal = 0
                }));
                foreach (var session in allSessions)
                {
                    if (session.PlayerId.HasValue && !session.Player.IsEliminated && session.Player.CurrentArea == shape.Area)
                    {
                        session.TrySend(detonationPacket);
                    }
                }
                eventLogs.LogSystem(matchingId, $"ORB_CROSSFIRE_DETONATE event={shape.EventId} owner={shape.OwnerId} " + $"at=({detonation.X:F2},{detonation.Y:F2}) radius={shape.BlastRadius:F2} visualOnly=true");
            }
            else
            {
                eventLogs.LogSystem(matchingId, $"ORB_CROSSFIRE_VANISH event={shape.EventId} owner={shape.OwnerId}");
            }
        }

        if (shapes.Count != shapesBefore)
        {
            runtime.SunOrbAttacks.DodgeSnapshot = BuildDodgeSnapshot(shapes);
        }
    }

    private static bool IsSwarmCrossfireSweptBody(SwarmCrossfireShape shape, Vector3f position, float fromFront, float toFront, float radiusPadding, float bodyHeight)
    {
        float length = shape.GroundLength;
        if (length <= 0f)
        {
            return false;
        }

        float ax = shape.Origin.X;
        float ay = shape.Origin.Y * SwarmGroundYScale;
        float ux = (shape.End.X - ax) / length;
        float uy = (shape.End.Y * SwarmGroundYScale - ay) / length;
        float reach = shape.HalfWidth + radiusPadding;
        float bodyStart = -Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y;
        float bodyEnd = bodyHeight - Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y;
        for (float bodyY = bodyStart; bodyY <= bodyEnd + 0.001f; bodyY += 0.45f)
        {
            float px = position.X;
            float py = (position.Y + bodyY) * SwarmGroundYScale;
            float along = (px - ax) * ux + (py - ay) * uy;
            float perpendicular = MathF.Abs((px - ax) * uy - (py - ay) * ux);
            if (perpendicular > reach)
            {
                continue;
            }

            float clampedAlong = Math.Clamp(along, 0f, length);
            float dxToSegment = along - clampedAlong;
            if (dxToSegment * dxToSegment + perpendicular * perpendicular > reach * reach)
            {
                continue;
            }

            if (along > fromFront && along <= toFront + radiusPadding)
            {
                return true;
            }
        }

        return false;
    }

    public void ProcessSwarmSunBurns(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Sun orb attacks require the match lock.");
        }

        if (runtime.IsEnded)
        {
            return;
        }
        var players = runtime.GetAlivePlayers();
        foreach (var victim in runtime.GetPlayers())
        {
            if (victim.SunBurn is not { } burn)
            {
                continue;
            }

            if (nowUtc >= burn.NextTickAtUtc)
            {
                combatDamage.ApplySwarmShock(runtime, healthService, burn.OwnerId, burn.WeaponItemId, burn.Area, victim.PlayerId, "SUN_BURN_TICK", players, Config.SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER, isPeriodicDamage: true);
                burn = burn with { NextTickAtUtc = burn.NextTickAtUtc.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS) };
                victim.SunBurn = burn;
            }

            if (nowUtc >= burn.UntilUtc)
            {
                victim.SunBurn = null;
            }
        }
    }
}
