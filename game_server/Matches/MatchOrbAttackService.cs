using game_server.matches.monsters;
using game_server.players;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.matches;

/// <summary>
///     발동된 오브 공격을 매치 틱에서 진행한다. 태양 공격의 이동·벽 폭발·화상과 파도 공격의 기폭·감속을 처리한다.
///     공격 상태는 MatchRuntime과 Player가 보관하며, 피해 적용은 MatchCombatDamageService에 위임한다.
/// </summary>
internal sealed class MatchOrbAttackService(
    PlayerHealthService healthService,
    MatchCombatDamageService combatDamage)
{
    private const float SwarmGroundYScale = GroundGeometry.GroundYScale;
    private const float SwarmCrossfirePlayerRadius = GroundGeometry.PlayerRadius;
    private const float SwarmCrossfirePlayerBodyHeight = 0.9f;
    private const float SwarmCrossfireMonsterRadius = GroundGeometry.MonsterRadius;
    private static readonly bool SwarmCrossfireEnabled = true;
    private static long _lastEventId;

    public static bool IsSunCrossfireWeapon(int weaponItemId) => SwarmCrossfireEnabled && OrbData.TryGetOrbGroupAndTier(weaponItemId, out var orbGroupId, out _) && orbGroupId == OrbGroupIds.Sun;
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

    public HashSet<(long OwnerId, long CombatTargetId)> CollectSunCrossfireAnchoredTargets(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Sun orb attacks require the match lock.");
        }
        var anchored = new HashSet<(long, long)>();
        foreach (var shape in runtime.SunCrossfireShapes)
        {
            anchored.Add((shape.OwnerId, shape.AnchorCombatTargetId));
        }
        return anchored;
    }

    public HashSet<long> CollectSunCrossfireCappedOwners(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Sun orb attacks require the match lock.");
        }
        var telegraphingByOwner = new Dictionary<long, int>();
        foreach (var shape in runtime.SunCrossfireShapes)
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

    public void ProcessSunCrossfires(MatchRuntime runtime, DateTime nowUtc)
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
        var allSessions = runtime.GetSessions().Where(session => !session.IsGameEnded).ToList();
        if (!SwarmCrossfireEnabled)
        {
            return;
        }

        var shapes = runtime.SunCrossfireShapes;
        IReadOnlyList<Monster>? monsterTargets = null;
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
            monsterTargets ??= runtime.Monsters.GetCombatTargets();

            foreach (var monster in monsterTargets)
            {
                if (monster.Area != shape.Area || shape.HitMonsters.Contains(monster.CombatTargetId))
                {
                    continue;
                }

                if (!IsSunCrossfireSweptBody(shape, monster.Position, lastFront, front, SwarmCrossfireMonsterRadius, GroundGeometry.MonsterBodyHeight))
                {
                    continue;
                }

                shape.HitMonsters.Add(monster.CombatTargetId);
                int monsterDamage = combatDamage.RollSwarmCriticalDamage(runtime, shape.Damage, out bool critical);
                combatDamage.ApplySwarmMonsterHitNow(runtime, monster.CombatTargetId, monster.MonsterId, shape.OwnerId, shape.WeaponItemId, shape.Area, monsterDamage, critical, nowUtc, allSessions);
            }

            foreach (var participant in players)
            {
                if (participant.IsEliminated || participant.Position == null)
                {
                    continue;
                }

                if (participant.PlayerId == shape.OwnerId || participant.GameInfo.ObjectInfo.Area != shape.Area || shape.HitVictims.Contains(participant.PlayerId))
                {
                    continue;
                }

                if (!IsSunCrossfireSweptBody(shape, participant.Position!, lastFront, front, SwarmCrossfirePlayerRadius, SwarmCrossfirePlayerBodyHeight))
                {
                    continue;
                }

                shape.HitVictims.Add(participant.PlayerId);
                combatDamage.ApplySwarmShock(runtime, healthService, shape.OwnerId, shape.WeaponItemId, shape.Area, participant.PlayerId, players);
                if (runtime.IsEnded)
                {
                    return;
                }
                participant.StatusEffects.SunBurn = new PlayerStatusEffects.SunBurnState(shape.OwnerId, shape.WeaponItemId, shape.Area, nowUtc.AddSeconds(Config.SWARM_SUN_BURN_SECONDS), nowUtc.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS));
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
                    if (session.PlayerId.HasValue && !session.Player.IsEliminated && session.Player.GameInfo.ObjectInfo.Area == shape.Area)
                    {
                        session.TrySend(detonationPacket);
                    }
                }
            }
            else
            {
            }
        }
    }

    private static bool IsSunCrossfireSweptBody(SwarmCrossfireShape shape, Vector3f position, float fromFront, float toFront, float radiusPadding, float bodyHeight)
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

    public void ProcessSunBurns(MatchRuntime runtime, DateTime nowUtc)
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
            if (victim.StatusEffects.SunBurn is not { } burn)
            {
                continue;
            }

            if (nowUtc >= burn.NextTickAtUtc)
            {
                combatDamage.ApplySwarmShock(runtime, healthService, burn.OwnerId, burn.WeaponItemId, burn.Area, victim.PlayerId, players, Config.SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER, isPeriodicDamage: true);
                burn = burn with { NextTickAtUtc = burn.NextTickAtUtc.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS) };
                victim.StatusEffects.SunBurn = burn;
            }

            if (nowUtc >= burn.UntilUtc)
            {
                victim.StatusEffects.SunBurn = null;
            }
        }
    }

    public void ProcessWaveDetonations(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Wave orb attacks require the match lock.");
        }
        if (runtime.IsEnded) return;
        for (int index = runtime.PendingWaveAttacks.Count - 1; index >= 0; index--)
        {
            var vortex = runtime.PendingWaveAttacks[index];
            if (nowUtc < vortex.ExplodeAtUtc)
                continue;
            runtime.PendingWaveAttacks.RemoveAt(index);

            var players = runtime.GetAlivePlayers();
            var owner = runtime.GetPlayer(vortex.OwnerId);
            foreach (var target in runtime.Monsters.GetCombatTargets())
            {
                if (target.Area != vortex.Area)
                {
                    continue;
                }
                if (!GroundGeometry.IsWithinGroundRadius(vortex.Position, target.Position, vortex.Radius))
                {
                    continue;
                }
                int monsterDamage = combatDamage.RollSwarmCriticalDamage(runtime, vortex.Damage, out bool critical);
                target.ReserveDamage(monsterDamage);
                combatDamage.ScheduleMonsterHit(runtime, new PendingMonsterHit(target.CombatTargetId, vortex.OwnerId, monsterDamage, nowUtc));
                if (vortex.AppliesSlow)
                {
                    target.ApplySlow(OrbData.WaveSlowSeconds, nowUtc);
                }

                int monsterId = (runtime.Monsters.FindAliveByCombatTarget(target.CombatTargetId)?.MonsterId ?? 0);
                if (monsterId <= 0)
                {
                    continue;
                }

                combatDamage.QueueMonsterHitNotification(runtime, owner, monsterId, vortex.Area, vortex.SourceItemId, monsterDamage, critical, showDamageOnly: true);
            }

            foreach (var participant in players)
            {
                if (participant.IsEliminated || participant.PlayerId == vortex.OwnerId || participant.GameInfo.ObjectInfo.Area != vortex.Area || participant.Position == null)
                {
                    continue;
                }

                if (!GroundGeometry.IsWithinGroundRadius(vortex.Position, participant.Position,
                        vortex.Radius + GroundGeometry.PlayerRadius))
                {
                    continue;
                }

                combatDamage.ApplySwarmShock(runtime, healthService, vortex.OwnerId, vortex.SourceItemId, vortex.Area, participant.PlayerId, players, Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER);
                if (runtime.IsEnded)
                {
                    return;
                }

                if (participant.IsEliminated || !vortex.AppliesSlow)
                {
                    continue;
                }
                participant.StatusEffects.Apply(PlayerStatusEffectKind.WaveSlow, nowUtc.AddSeconds(OrbData.WaveSlowSeconds));
                var victimSession = participant.Session;
                if (victimSession != null && vortex.OwnerId != 0)
                {
                    using var packet = PacketMaker.G_TO_C_STATUS_EFFECT(new G_TO_C_STATUS_EFFECT
                    {
                        SourcePlayerId = vortex.OwnerId,
                        TargetPlayerId = participant.PlayerId,
                        AreaType = vortex.Area,
                        Effect = CombatStatusEffectKind.WaveOrbSlow,
                        DurationMs = (int)(OrbData.WaveSlowSeconds * 1000f)
                    });
                    victimSession.TrySend(packet);
                }
            }
        }
    }
}
