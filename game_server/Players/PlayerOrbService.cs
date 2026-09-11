using game_server.matches;
using game_server.matches.combat;
using game_server.matches.logging;
using game_server.matches.monsters;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.players;

/// <summary>
///     매치 잠금 안에서 플레이어가 보유한 오브의 공격 발동·회복을 처리한다.
///     플레이어별 발동 시각은 Player가, 지속 중인 공격과 오브별 회복 시각은 MatchRuntime이 소유한다.
/// </summary>
internal sealed class PlayerOrbService(
    PlayerHealthService healthService,
    MatchCombatDamageService combatDamage,
    PlayerOrbTrailService orbTrails,
    GameEventLogManager eventLogs)
{
    private const float SwarmGroundYScale = SwarmCombatGeometry.GroundYScale;
    private const float SwarmCrossfireMonsterRadius = SwarmCombatGeometry.MonsterRadius;
    private const float SwarmCrossfireMonsterBodyHeight = 0.6f;
    private const int OrbRingEffectKindWaveOrb = 2;
    private const float SwarmCrossfireWallProbeStep = 0.2f;
    private const float SwarmCrossfireMaxGroundLength = 40f;
    private static double SwarmWindBladeVictimImmuneSeconds => SwarmConfigData.GetDouble("SWARM_WIND_BLADE_VICTIM_IMMUNE_SECONDS", 0.9d);
    private static double WaveOrbAttackIntervalSeconds => SwarmConfigData.GetDouble("SWARM_WAVE_VORTEX_INTERVAL_SECONDS", 2d);
    private static double WaveOrbDetonationDelaySeconds => SwarmConfigData.GetDouble("SWARM_WAVE_VORTEX_FUSE_SECONDS", 0.65d);

    public void ProcessOrbRecovery(MatchRuntime runtime, IReadOnlyCollection<ProximityCombatActor> orbActors, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb recovery requires the match lock.");
        }

        if (runtime.IsEnded)
        {
            return;
        }

        var nextRecoveryAtUtcByOrb = runtime.OrbRecoveryReadyAtUtc;
        var activeRecoveryOrbs = new HashSet<(long PlayerId, long ItemUid, int StackIndex)>();
        var recoveryByPlayer = new Dictionary<long, List<(ProximityCombatActor OrbActor, int Amount)>>();

        foreach (var orbActor in orbActors)
        {
            int requestedRecovery = OrbData.GetRecoveryAmount(orbActor.WeaponItemId);
            if (requestedRecovery <= 0)
            {
                continue;
            }

            var recoveryKey = (orbActor.PlayerId, orbActor.WeaponItemUid, orbActor.WeaponStackIndex);
            activeRecoveryOrbs.Add(recoveryKey);
            if (!nextRecoveryAtUtcByOrb.TryGetValue(recoveryKey, out var nextRecoveryAtUtc))
            {
                nextRecoveryAtUtcByOrb[recoveryKey] = nowUtc.AddSeconds(OrbData.RecoveryTickSeconds);
                continue;
            }

            if (nowUtc < nextRecoveryAtUtc)
            {
                continue;
            }

            nextRecoveryAtUtcByOrb[recoveryKey] = nowUtc.AddSeconds(OrbData.RecoveryTickSeconds);
            if (!recoveryByPlayer.TryGetValue(orbActor.PlayerId, out var dueRecoveries))
            {
                dueRecoveries = [];
                recoveryByPlayer[orbActor.PlayerId] = dueRecoveries;
            }

            dueRecoveries.Add((orbActor, requestedRecovery));
        }

        foreach (var (playerId, dueRecoveries) in recoveryByPlayer)
        {
            var player = runtime.GetParticipant(playerId);
            if (player == null || player.IsEliminated)
            {
                continue;
            }

            int requestedRecovery = dueRecoveries.Sum(entry => entry.Amount);
            var representative = dueRecoveries
                .OrderByDescending(entry => entry.Amount)
                .ThenBy(entry => entry.OrbActor.WeaponItemUid)
                .First().OrbActor;
            var change = healthService.Recover(runtime, player, requestedRecovery);
            if (change.Recovered <= 0)
            {
                continue;
            }

            using var packet = PacketMaker.G_TO_C_HEALTH_RECOVERY(new()
            {
                PlayerId = playerId,
                AreaType = player.CurrentArea,
                Amount = change.Recovered,
                Source = HealthRecoveryKind.Orb,
                OrbItemId = representative.WeaponItemId
            });
            player.Session?.TrySend(packet);
        }

        foreach (var recoveryKey in nextRecoveryAtUtcByOrb.Keys.ToArray())
        {
            if (!activeRecoveryOrbs.Contains((recoveryKey.PlayerId, recoveryKey.ItemUid, recoveryKey.StackIndex)))
            {
                nextRecoveryAtUtcByOrb.Remove(recoveryKey);
            }
        }
    }

    public void ActivateWaveOrbs(MatchRuntime runtime, Player owner, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb attack activation requires the match lock.");
        }

        if (runtime.IsEnded || owner.IsEliminated || owner.Position == null)
        {
            return;
        }

        if (!ReferenceEquals(runtime.GetParticipant(owner.PlayerId), owner))
        {
            throw new InvalidOperationException("Orb attack owner must belong to the match.");
        }

        var alivePlayers = runtime.GetAlivePlayers();
        IReadOnlyList<SwarmArenaCombatTarget>? monsterTargets = null;
        var orderedOrbs = owner.Orbs.GetOrderedOrbs();
        if (orderedOrbs.Count == 0)
        {
            return;
        }

        float sunDamageMultiplier = -1f;
        List<int>? orbTiers = null;
        for (int ordinal = 0; ordinal < orderedOrbs.Count; ordinal++)
        {
            var orb = orderedOrbs[ordinal];
            if (!OrbData.TryGetColorAndTier(orb.ItemId, out var color, out _) || color != OrbColor.Blue)
            {
                continue;
            }

            long orbUid = orb.ItemUid;
            double firstPhase = 0.5d + orb.ItemUid % 977 / 977d;
            if (!owner.IsWaveOrbDue(orbUid, nowUtc, WaveOrbAttackIntervalSeconds, firstPhase))
            {
                continue;
            }

            float radius = OrbData.GetSwarmWaveBombRadius(orb.ItemId);
            int baseDamage = OrbData.GetSwarmPveAttackDamage(orb.ItemId);
            if (radius <= 0f || baseDamage <= 0)
            {
                continue;
            }

            orbTiers ??= orbTrails.GetOrbTiersInOrder(runtime, owner);
            var orbPosition = orbTrails.GetOrbPosition(runtime, owner, ordinal, owner.Position!, orbTiers);
            monsterTargets ??= runtime.Monsters.GetCombatTargets();
            bool hasTargetInRange = false;
            foreach (var monsterTarget in monsterTargets)
            {
                if (monsterTarget.Area != owner.CurrentArea)
                {
                    continue;
                }
                if (!SwarmCombatGeometry.IsWithinGroundRadius(orbPosition, monsterTarget.Position, radius + SwarmCombatGeometry.MonsterRadius))
                {
                    continue;
                }
                hasTargetInRange = true;
                break;
            }

            if (!hasTargetInRange)
            {
                foreach (var participant in alivePlayers)
                {
                    if (participant.Position == null)
                    {
                        continue;
                    }
                    if (participant.PlayerId == owner.PlayerId)
                    {
                        continue;
                    }
                    if (participant.CurrentArea != owner.CurrentArea)
                    {
                        continue;
                    }
                    if (!SwarmCombatGeometry.IsWithinGroundRadius(orbPosition, participant.Position, radius + SwarmCombatGeometry.PlayerRadius))
                    {
                        continue;
                    }
                    hasTargetInRange = true;
                    break;
                }
            }

            if (!hasTargetInRange)
            {
                continue;
            }

            owner.ScheduleNextWaveOrbAttack(orbUid, nowUtc, WaveOrbAttackIntervalSeconds);

            if (sunDamageMultiplier < 0f)
            {
                sunDamageMultiplier = OrbData.GetSunPveAttackMultiplier(orderedOrbs);
            }
            int damage = Math.Max(1, (int)MathF.Round(baseDamage * sunDamageMultiplier * Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER));
            runtime.PendingWaveAttacks.Add(new PendingWaveAttack(owner.PlayerId, owner.CurrentArea, orbPosition, damage, radius, orb.ItemId, nowUtc.AddSeconds(WaveOrbDetonationDelaySeconds)));
            using var packet = Packet.Create((int)Protocol.G_TO_C_ORB_RING_EFFECT);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_RING_EFFECT
            {
                OwnerPlayerId = owner.PlayerId,
                CenterX = orbPosition.X,
                CenterY = orbPosition.Y,
                Radius = radius,
                Kind = OrbRingEffectKindWaveOrb,
                FromOrdinal = ordinal
            }));
            foreach (var session in runtime.GetSessions())
            {
                if (session is { PlayerId: not null, IsGameEnded: false } &&
                    session.Player.CurrentArea == owner.CurrentArea)
                {
                    session.TrySend(packet);
                }
            }
        }
    }

    public bool TryStartSunCrossfire(MatchRuntime runtime, Player owner, ProximityCombatAttack attack, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Sun orb attacks require the match lock.");
        }

        if (runtime.IsEnded || owner.IsEliminated)
        {
            return false;
        }

        if (!ReferenceEquals(runtime.GetParticipant(owner.PlayerId), owner) || attack.AttackerPlayerId != owner.PlayerId)
        {
            throw new InvalidOperationException("Sun orb attack owner must belong to the match and match the attack.");
        }
        if (owner.Position == null || !SunOrbAttackService.IsSwarmCrossfireWeapon(attack.WeaponItemId))
        {
            return false;
        }

        long matchingId = runtime.MatchingId;
        var monsterTargets = runtime.Monsters.GetCombatTargets();
        var origin = attack.Origin ?? owner.Position;
        var anchor = attack.AnchorPosition;
        int anchorMonsterId = runtime.Monsters.GetMonsterIdForCombatTarget(attack.TargetPlayerId);
        if (anchor == null && runtime.GetParticipant(attack.TargetPlayerId)?.Position is { } targetPosition)
        {
            anchor = targetPosition;
        }

        if (anchor == null)
        {
            foreach (var monsterTarget in monsterTargets)
            {
                if (monsterTarget.CombatTargetId != attack.TargetPlayerId)
                {
                    continue;
                }

                anchor = monsterTarget.Position;
                break;
            }
        }

        if (anchor == null)
        {
            return false;
        }

        OrbData.TryGetColorAndTier(attack.WeaponItemId, out _, out int tier);
        var sunOrbAttacks = runtime.SunOrbAttacks;
        if (sunOrbAttacks.CountTelegraphing(owner.PlayerId, nowUtc) >= Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER)
        {
            return false;
        }

        float targetGroundX = anchor.X - origin.X;
        float targetGroundY = (anchor.Y - origin.Y) * SwarmGroundYScale;
        if (targetGroundX * targetGroundX + targetGroundY * targetGroundY < 0.0025f)
        {
            return false;
        }

        int tierIndex = Math.Clamp(tier, 1, 3) - 1;
        float width = Config.SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER[tierIndex];
        float halfWidth = width * 0.5f;
        float blastRadius = Config.SWARM_CROSSFIRE_SUN_BLAST_RADIUS_BY_TIER[tierIndex];
        float sweepSpeed = Config.SWARM_CROSSFIRE_SUN_SWEEP_SPEED;

        const float diagonalUnit = 0.70710677f;
        ReadOnlySpan<float> directionX = [diagonalUnit, -diagonalUnit, -diagonalUnit, diagonalUnit];
        ReadOnlySpan<float> directionY = [diagonalUnit, -diagonalUnit, diagonalUnit, -diagonalUnit];

        float selectedDirectionX = 0f;
        float selectedDirectionY = 0f;
        float selectedLength = 0f;
        int bestTargetCount = 0;
        float nearestTargetAlong = float.MaxValue;
        float fallbackProjection = float.MinValue;
        bool hasSelectedDirection = false;
        bool hasTargetedDirection = false;
        for (int direction = 0; direction < 4; direction++)
        {
            float candidateLength = SwarmCrossfireMaxGroundLength;
            for (float along = SwarmCrossfireWallProbeStep; along < SwarmCrossfireMaxGroundLength;
                 along += SwarmCrossfireWallProbeStep)
            {
                var probe = new Vector3f(
                    origin.X + directionX[direction] * along,
                    origin.Y + directionY[direction] * along / SwarmGroundYScale,
                    0f);
                var cell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, probe);
                if (GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell) == attack.Area)
                {
                    continue;
                }

                candidateLength = along;
                break;
            }
            if (candidateLength < 0.3f)
            {
                continue;
            }

            int hits = 0;
            float nearest = float.MaxValue;
            float reach = halfWidth + SwarmCrossfireMonsterRadius;
            float bodyStart = -Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y;
            float bodyEnd = SwarmCrossfireMonsterBodyHeight - Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y;
            foreach (var monsterTarget in monsterTargets)
            {
                if (monsterTarget.Area != attack.Area)
                {
                    continue;
                }

                float monsterNearestAlong = float.MaxValue;
                for (float bodyY = bodyStart; bodyY <= bodyEnd + 0.001f; bodyY += 0.45f)
                {
                    float relativeX = monsterTarget.Position.X - origin.X;
                    float relativeY = (monsterTarget.Position.Y + bodyY - origin.Y) * SwarmGroundYScale;
                    float targetAlong = relativeX * directionX[direction] + relativeY * directionY[direction];
                    if (targetAlong < 0f || targetAlong > candidateLength)
                    {
                        continue;
                    }

                    float perpendicular = MathF.Abs(relativeX * directionY[direction] - relativeY * directionX[direction]);
                    if (perpendicular > reach)
                    {
                        continue;
                    }

                    monsterNearestAlong = MathF.Min(monsterNearestAlong, targetAlong);
                }

                if (monsterNearestAlong >= float.MaxValue)
                {
                    continue;
                }

                hits++;
                nearest = MathF.Min(nearest, monsterNearestAlong);
            }
            bool isBetterTargetDirection = hits > 0 && (!hasTargetedDirection || nearest < nearestTargetAlong - 0.001f || (MathF.Abs(nearest - nearestTargetAlong) <= 0.001f && hits > bestTargetCount));
            if (isBetterTargetDirection)
            {
                bestTargetCount = hits;
                nearestTargetAlong = nearest;
                selectedDirectionX = directionX[direction];
                selectedDirectionY = directionY[direction];
                selectedLength = candidateLength;
                hasSelectedDirection = true;
                hasTargetedDirection = true;
            }

            if (hasTargetedDirection)
            {
                continue;
            }
            float projection = targetGroundX * directionX[direction] + targetGroundY * directionY[direction];
            if (projection > fallbackProjection)
            {
                fallbackProjection = projection;
                selectedDirectionX = directionX[direction];
                selectedDirectionY = directionY[direction];
                selectedLength = candidateLength;
                hasSelectedDirection = true;
            }
        }

        if (!hasSelectedDirection)
        {
            return false;
        }

        bool detonateAtWall = selectedLength < SwarmCrossfireMaxGroundLength - 0.01f;
        var endPosition = new Vector3f(origin.X + selectedDirectionX * selectedLength, origin.Y + selectedDirectionY * selectedLength / SwarmGroundYScale, 0f);

        float sweepSeconds = (selectedLength + width) / sweepSpeed;
        long eventId = sunOrbAttacks.AllocateEventId();
        var telegraphEndsAtUtc = nowUtc.AddSeconds(Config.SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS);
        sunOrbAttacks.AddShape(new SwarmCrossfireShape
        {
            EventId = eventId,
            OwnerId = attack.AttackerPlayerId,
            WeaponItemId = attack.WeaponItemId,
            Damage = attack.Damage,
            Area = attack.Area,
            Origin = new Vector3f(origin.X, origin.Y, 0f),
            End = endPosition,
            GroundLength = selectedLength,
            HalfWidth = halfWidth,
            BlastRadius = blastRadius,
            SweepSpeed = sweepSpeed,
            ArmedAtUtc = telegraphEndsAtUtc,
            ExpiresAtUtc = telegraphEndsAtUtc.AddSeconds(sweepSeconds),
            DetonateAtWall = detonateAtWall,
            AnchorMonsterId = anchorMonsterId,
            AnchorCombatTargetId = attack.TargetPlayerId,
            LastFront = -halfWidth
        });

        using var packet = Packet.Create((int)Protocol.G_TO_C_SUN_ORB_ATTACK);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUN_ORB_ATTACK
        {
            EventId = eventId,
            OwnerPlayerId = owner.PlayerId,
            WeaponItemId = attack.WeaponItemId,
            Shape = Config.SWARM_CROSSFIRE_SHAPE_LINE,
            OriginX = origin.X,
            OriginY = origin.Y,
            EndX = endPosition.X,
            EndY = endPosition.Y,
            Width = width,
            TelegraphSeconds = Config.SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS,
            ActiveSeconds = sweepSeconds,
            AnchorMonsterId = anchorMonsterId,
            OwnerOrbOrdinal = attack.AttackerTrailOrdinal
        }));
        foreach (var session in runtime.GetSessions())
        {
            if (session is { PlayerId: not null, Player.IsEliminated: false } && session.Player.CurrentArea == attack.Area)
            {
                session.TrySend(packet);
            }
        }
        return true;
    }

    public void ActivateWindOrbs(MatchRuntime runtime, Player owner, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Wind orb attacks require the match lock.");
        }

        if (runtime.IsEnded || owner.IsEliminated || owner.Position == null)
        {
            return;
        }

        if (!ReferenceEquals(runtime.GetParticipant(owner.PlayerId), owner))
        {
            throw new InvalidOperationException("Wind orb attack owner must belong to the match.");
        }

        long matchingId = runtime.MatchingId;
        var alivePlayers = runtime.GetAlivePlayers();
        var activeSessions = runtime.GetSessions().Where(session => !session.IsGameEnded).ToList();
        IReadOnlyList<SwarmArenaCombatTarget>? monsterTargets = null;
        var windAttackState = runtime.WindOrbAttacks;
        var orderedOrbs = owner.Orbs.GetOrderedOrbs();
        if (orderedOrbs.Count == 0)
        {
            return;
        }

        float sunDamageMultiplier = -1f;
        List<int>? orbTiers = null;
        for (int ordinal = 0; ordinal < orderedOrbs.Count; ordinal++)
        {
            var orb = orderedOrbs[ordinal];
            if (!OrbData.TryGetColorAndTier(orb.ItemId, out var color, out int tier) || color != OrbColor.Green)
            {
                continue;
            }

            if (!owner.TryBeginWindOrbTick(orb.ItemUid, nowUtc, Config.SWARM_WIND_BLADE_TICK_SECONDS))
            {
                continue;
            }

            orbTiers ??= orbTrails.GetOrbTiersInOrder(runtime, owner);
            var orbPosition = orbTrails.GetOrbPosition(runtime, owner, ordinal, owner.Position!, orbTiers);
            float radius = Config.SWARM_WIND_BLADE_RADIUS_BY_TIER[Math.Clamp(tier, 1, 3) - 1];
            monsterTargets ??= runtime.Monsters.GetCombatTargets();
            List<SwarmArenaCombatTarget>? monstersInRadius = null;
            foreach (var monster in monsterTargets)
            {
                if (monster.Area != owner.CurrentArea)
                {
                    continue;
                }

                if (!SwarmCombatGeometry.IsWithinGroundRadius(orbPosition, monster.Position, radius + SwarmCombatGeometry.MonsterRadius))
                {
                    continue;
                }
                monstersInRadius ??= [];
                monstersInRadius.Add(monster);
            }

            List<Player>? playersInRadius = null;
            foreach (var participant in alivePlayers)
            {
                if (participant.Position == null)
                {
                    continue;
                }

                if (participant.PlayerId == owner.PlayerId || participant.CurrentArea != owner.CurrentArea)
                {
                    continue;
                }

                if (!SwarmCombatGeometry.IsWithinGroundRadius(orbPosition, participant.Position!, radius + SwarmCombatGeometry.PlayerRadius))
                {
                    continue;
                }
                playersInRadius ??= [];
                playersInRadius.Add(participant);
            }

            if (monstersInRadius == null && playersInRadius == null)
            {
                owner.ResetWindOrbEngagement(orb.ItemUid);
                continue;
            }

            if (!owner.HasCompletedWindOrbSpinup(orb.ItemUid, nowUtc, Config.SWARM_WIND_BLADE_SPINUP_SECONDS))
            {
                continue;
            }

            if (sunDamageMultiplier < 0f)
            {
                sunDamageMultiplier = OrbData.GetSunPveAttackMultiplier(orderedOrbs);
            }

            int damage = Math.Max(1, (int)MathF.Round(OrbData.GetSwarmPveAttackDamage(orb.ItemId) * sunDamageMultiplier * Config.SWARM_WIND_BLADE_DAMAGE_MULTIPLIER));
            int monsterHits = 0;
            if (monstersInRadius != null)
            {
                foreach (var monster in monstersInRadius)
                {
                    monsterHits++;
                    runtime.Monsters.RecordMonsterAttackEvent(monster.CombatTargetId);
                    int monsterDamage = combatDamage.RollSwarmCriticalDamage(runtime, damage, out bool critical);
                    combatDamage.ApplySwarmMonsterHitNow(runtime, monster.CombatTargetId, monster.MonsterId, owner.PlayerId, orb.ItemId, owner.CurrentArea, monsterDamage, critical, activeSessions);
                }
            }

            int shocks = 0;
            if (playersInRadius != null)
            {
                foreach (var participant in playersInRadius)
                {
                    if (!windAttackState.TryClaimVictimShock(participant.PlayerId, nowUtc, SwarmWindBladeVictimImmuneSeconds))
                    {
                        continue;
                    }

                    shocks++;
                    combatDamage.ApplySwarmShock(runtime, healthService, owner.PlayerId, orb.ItemId, owner.CurrentArea, participant.PlayerId, $"WIND_BLADE_HIT ordinal={ordinal}", alivePlayers);
                    if (runtime.IsEnded)
                    {
                        return;
                    }

                    windAttackState.ApplyWound(participant.PlayerId, nowUtc.AddSeconds(Config.SWARM_WIND_WOUND_SECONDS));
                    var victimSession = participant.Session;
                    if (victimSession is not { PlayerId: not null })
                    {
                        continue;
                    }

                    using var packet = PacketMaker.G_TO_C_STATUS_EFFECT(new()
                    {
                        SourcePlayerId = owner.PlayerId,
                        TargetPlayerId = participant.PlayerId,
                        AreaType = owner.CurrentArea,
                        Effect = CombatStatusEffectKind.WindOrbWound,
                        DurationMs = (int)(Config.SWARM_WIND_WOUND_SECONDS * 1000f)
                    });
                    victimSession.TrySend(packet);
                }
            }

            if (monsterHits > 0 || shocks > 0)
            {
                eventLogs.LogSystem(matchingId, $"WIND_BLADE owner={owner.PlayerId} ordinal={ordinal} at=({orbPosition.X:F2},{orbPosition.Y:F2}) " + $"radius={radius:F2} damage={damage} monsters={monsterHits} shocks={shocks}");
            }
        }
    }
}
