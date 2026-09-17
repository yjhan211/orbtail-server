using game_server.matches.monsters;
using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     발동된 오브 공격을 처리한다. 태양 공격의 이동·화상, 파도 공격의 기폭·감속, 바람 공격의 즉시 피해·상처를 담당한다.
///     공격 상태는 MatchRuntime과 Player가 보관하며, 피해 적용은 MatchCombatDamageService에 위임한다.
/// </summary>
internal sealed class MatchOrbAttackService(
    PlayerHealthService healthService,
    MatchCombatDamageService combatDamage)
{
    private static long _lastEventId;

    public static long AllocateEventId() => Interlocked.Increment(ref _lastEventId);


    internal void ProcessWindAttack(MatchRuntime runtime, Player owner, int weaponItemId, Vector3f orbPosition, float radius, int damage, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Wind orb attacks require the match lock.");
        }
        if (runtime.IsEnded)
        {
            return;
        }
        var ownerArea = owner.GameInfo.ObjectInfo.Area;
        var (monsters, players) = CollectTargetsInRadius(runtime, owner.PlayerId, ownerArea, orbPosition, radius, padBodyRadius: true);
        if (monsters.Count == 0 && players.Count == 0)
        {
            return;
        }

        if (monsters.Count > 0)
        {
            var activeSessions = runtime.GetSessions().Where(session => !session.IsGameEnded).ToList();
            foreach (var monster in monsters)
            {
                int monsterDamage = combatDamage.RollSwarmCriticalDamage(runtime, damage, out bool critical);
                combatDamage.ApplySwarmMonsterHitNow(runtime, monster.MonsterId, owner.PlayerId, weaponItemId, ownerArea, monsterDamage, critical, nowUtc, activeSessions);
            }
        }

        if (players.Count == 0)
        {
            return;
        }

        var alivePlayers = runtime.GetAlivePlayers();
        foreach (var participant in players)
        {
            if (!participant.StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc, Config.SWARM_WIND_BLADE_VICTIM_IMMUNE_SECONDS))
            {
                continue;
            }

            combatDamage.ApplySwarmShock(runtime, healthService, owner.PlayerId, weaponItemId, ownerArea, participant.PlayerId, alivePlayers);
            if (runtime.IsEnded)
            {
                return;
            }

            if (participant.IsEliminated)
            {
                continue;
            }

            participant.StatusEffects.Apply(PlayerStatusEffectKind.Wound, nowUtc.AddSeconds(Config.SWARM_WIND_WOUND_SECONDS));
            var victimSession = participant.Session;
            if (victimSession is not { PlayerId: not null })
            {
                continue;
            }

            combatDamage.QueueSessionEffect(runtime, victimSession, Protocol.G_TO_C_STATUS_EFFECT, new G_TO_C_STATUS_EFFECT
            {
                SourcePlayerId = owner.PlayerId,
                TargetPlayerId = participant.PlayerId,
                AreaType = ownerArea,
                Effect = CombatStatusEffectKind.WindOrbWound,
                DurationMs = (int)(Config.SWARM_WIND_WOUND_SECONDS * 1000f)
            });
        }
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
            float front = nowUtc >= shape.ExpiresAtUtc ? sweepEnd : -shape.HalfWidth + (float)(nowUtc - shape.ArmedAtUtc).TotalSeconds * Config.SWARM_CROSSFIRE_SUN_SWEEP_SPEED;
            front = MathF.Min(front, sweepEnd);
            float lastFront = shape.LastFront;
            shape.LastFront = front;
            monsterTargets ??= runtime.Monsters.GetCombatTargets();

            foreach (var monster in monsterTargets)
            {
                if (monster.Area != shape.Area || shape.HitMonsters.Contains(monster.MonsterId))
                {
                    continue;
                }

                if (!IsSunCrossfireSweptBody(shape, monster.Position, lastFront, front, GroundGeometry.MonsterRadius, GroundGeometry.MonsterBodyHeight))
                {
                    continue;
                }

                shape.HitMonsters.Add(monster.MonsterId);
                int monsterDamage = combatDamage.RollSwarmCriticalDamage(runtime, shape.Damage, out bool critical);
                combatDamage.ApplySwarmMonsterHitNow(runtime, monster.MonsterId, shape.OwnerId, shape.WeaponItemId, shape.Area, monsterDamage, critical, nowUtc, allSessions);
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

                if (!IsSunCrossfireSweptBody(shape, participant.Position!, lastFront, front, GroundGeometry.PlayerRadius, GroundGeometry.PlayerBodyHeight))
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
                    combatDamage.QueueSessionEffect(runtime, victimSession, Protocol.G_TO_C_STATUS_EFFECT, new G_TO_C_STATUS_EFFECT
                    {
                        SourcePlayerId = shape.OwnerId,
                        TargetPlayerId = participant.PlayerId,
                        AreaType = shape.Area,
                        Effect = CombatStatusEffectKind.SunBurn,
                        DurationMs = (int)(Config.SWARM_SUN_BURN_SECONDS * 1000f)
                    });
                }
            }

            if (front < sweepEnd)
                continue;

            shapes.RemoveAt(index);
        }
    }

    private static bool IsSunCrossfireSweptBody(SwarmCrossfireShape shape, Vector3f position, float fromFront, float toFront, float radiusPadding, float bodyHeight)
    {
        var origin = shape.Origin;
        var end = shape.End;
        float length = GroundGeometry.GroundDistance(origin, end);
        if (length <= 0f)
        {
            return false;
        }

        float originGroundY = origin.Y * GroundGeometry.GroundYScale;
        float unitX = (end.X - origin.X) / length;
        float unitY = (end.Y * GroundGeometry.GroundYScale - originGroundY) / length;
        return GroundGeometry.TryGetNearestBodyAlongOnLine(origin.X, originGroundY, unitX, unitY, length, shape.HalfWidth + radiusPadding, position, bodyHeight, out float along)
               && along > fromFront && along <= toFront + radiusPadding;
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

    internal static bool HasTargetInRadius(MatchRuntime runtime, long ownerId, AreaType area, Vector3f center, float radius)
    {
        foreach (var monster in runtime.Monsters.GetCombatTargets())
        {
            if (monster.Area == area && GroundGeometry.IsWithinGroundRadius(center, monster.Position, radius + GroundGeometry.MonsterRadius))
            {
                return true;
            }
        }
        foreach (var player in runtime.GetAlivePlayers())
        {
            if (player.PlayerId == ownerId || player.Position == null || player.GameInfo.ObjectInfo.Area != area)
            {
                continue;
            }
            if (GroundGeometry.IsWithinGroundRadius(center, player.Position, radius + GroundGeometry.PlayerRadius))
            {
                return true;
            }
        }
        return false;
    }

    internal static (List<Monster> Monsters, List<Player> Players) CollectTargetsInRadius(MatchRuntime runtime, long ownerId, AreaType area, Vector3f center, float radius, bool padBodyRadius)
    {
        float monsterRadius = padBodyRadius ? radius + GroundGeometry.MonsterRadius : radius;
        var monsters = new List<Monster>();
        foreach (var monster in runtime.Monsters.GetCombatTargets())
        {
            if (monster.Area == area && GroundGeometry.IsWithinGroundRadius(center, monster.Position, monsterRadius))
            {
                monsters.Add(monster);
            }
        }

        float playerRadius = padBodyRadius ? radius + GroundGeometry.PlayerRadius : radius;
        var players = new List<Player>();
        foreach (var participant in runtime.GetAlivePlayers())
        {
            if (participant.PlayerId == ownerId || participant.Position == null || participant.GameInfo.ObjectInfo.Area != area)
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

            var owner = runtime.GetPlayer(vortex.OwnerId);
            var (monsters, players) = CollectTargetsInRadius(runtime, vortex.OwnerId, vortex.Area, vortex.Position, vortex.Radius, padBodyRadius: true);
            foreach (var target in monsters)
            {
                int monsterDamage = combatDamage.RollSwarmCriticalDamage(runtime, vortex.Damage, out bool critical);
                target.ReserveDamage(monsterDamage);
                combatDamage.ScheduleMonsterHit(runtime, new PendingMonsterHit(target.MonsterId, vortex.OwnerId, monsterDamage, nowUtc));
                if (vortex.AppliesSlow)
                {
                    target.ApplySlow(Config.SWARM_WAVE_SLOW_SECONDS, nowUtc);
                }

                combatDamage.QueueMonsterHitNotification(runtime, owner, target.MonsterId, vortex.Area, vortex.SourceItemId, monsterDamage, critical, showDamageOnly: true);
            }

            var alivePlayers = runtime.GetAlivePlayers();
            foreach (var participant in players)
            {
                combatDamage.ApplySwarmShock(runtime, healthService, vortex.OwnerId, vortex.SourceItemId, vortex.Area, participant.PlayerId, alivePlayers, Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER);
                if (runtime.IsEnded)
                {
                    return;
                }

                if (participant.IsEliminated || !vortex.AppliesSlow)
                {
                    continue;
                }
                participant.StatusEffects.Apply(PlayerStatusEffectKind.WaveSlow, nowUtc.AddSeconds(Config.SWARM_WAVE_SLOW_SECONDS));
                var victimSession = participant.Session;
                if (victimSession != null && vortex.OwnerId != 0)
                {
                    combatDamage.QueueSessionEffect(runtime, victimSession, Protocol.G_TO_C_STATUS_EFFECT, new G_TO_C_STATUS_EFFECT
                    {
                        SourcePlayerId = vortex.OwnerId,
                        TargetPlayerId = participant.PlayerId,
                        AreaType = vortex.Area,
                        Effect = CombatStatusEffectKind.WaveOrbSlow,
                        DurationMs = (int)(Config.SWARM_WAVE_SLOW_SECONDS * 1000f)
                    });
                }
            }
        }
    }
}
