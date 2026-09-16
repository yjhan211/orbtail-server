using game_server.matches;
using game_server.matches.monsters;
using game_server.sessions;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.players;

/// <summary>
///     매치 잠금 안에서 플레이어가 보유한 오브의 공격 발동을 처리한다.
///     발동 시각은 PlayerOrbState가, 지속 중인 공격은 MatchRuntime이 소유한다.
/// </summary>
internal sealed class PlayerOrbService(
    PlayerHealthService healthService,
    MatchCombatDamageService combatDamage,
    PlayerOrbTrailService orbTrails)
{
    private const float AreaBoundaryProbeStep = 0.2f;
    private static double SwarmWindBladeVictimImmuneSeconds => SwarmConfigData.GetDouble("SWARM_WIND_BLADE_VICTIM_IMMUNE_SECONDS", 0.9d);
    private static double WaveOrbAttackIntervalSeconds => SwarmConfigData.GetDouble("SWARM_WAVE_VORTEX_INTERVAL_SECONDS", 2d);
    private static double WaveOrbDetonationDelaySeconds => SwarmConfigData.GetDouble("SWARM_WAVE_VORTEX_FUSE_SECONDS", 0.65d);

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

        if (!ReferenceEquals(runtime.GetPlayer(owner.PlayerId), owner))
        {
            throw new InvalidOperationException("Orb attack owner must belong to the match.");
        }

        var ownerArea = owner.GameInfo.ObjectInfo.Area;
        List<Player>? alivePlayers = null;
        IReadOnlyList<Monster>? monsterTargets = null;
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
            if (!owner.Orbs.UpdateWaveOrbAttackReadiness(orbUid, nowUtc, WaveOrbAttackIntervalSeconds, firstPhase))
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
                if (monsterTarget.Area != ownerArea)
                {
                    continue;
                }
                if (!GroundGeometry.IsWithinGroundRadius(orbPosition, monsterTarget.Position, radius + GroundGeometry.MonsterRadius))
                {
                    continue;
                }
                hasTargetInRange = true;
                break;
            }

            if (!hasTargetInRange)
            {
                alivePlayers ??= runtime.GetAlivePlayers();
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
                    if (participant.GameInfo.ObjectInfo.Area != ownerArea)
                    {
                        continue;
                    }
                    if (!GroundGeometry.IsWithinGroundRadius(orbPosition, participant.Position, radius + GroundGeometry.PlayerRadius))
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

            owner.Orbs.ScheduleNextWaveOrbAttack(orbUid, nowUtc, WaveOrbAttackIntervalSeconds);

            if (sunDamageMultiplier < 0f)
            {
                sunDamageMultiplier = OrbData.GetSunPveAttackMultiplier(orderedOrbs);
            }
            int damage = Math.Max(1, (int)MathF.Round(baseDamage * sunDamageMultiplier * Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER));
            bool appliesSlow = OrbData.IsResonating(orderedOrbs, OrbColor.Blue);
            runtime.PendingWaveAttacks.Add(new PendingWaveAttack(owner.PlayerId, ownerArea, orbPosition, damage, radius, orb.ItemId, nowUtc.AddSeconds(WaveOrbDetonationDelaySeconds), appliesSlow));
            using var packet = Packet.Create((int)Protocol.G_TO_C_ORB_RING_EFFECT);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_RING_EFFECT
            {
                OwnerPlayerId = owner.PlayerId,
                CenterX = orbPosition.X,
                CenterY = orbPosition.Y,
                Radius = radius,
                Kind = (int)OrbRingEffectKind.WaveOrb,
                FromOrdinal = ordinal
            }));
            foreach (var session in runtime.GetSessions())
            {
                if (session is { PlayerId: not null, IsGameEnded: false } &&
                    session.Player.GameInfo.ObjectInfo.Area == ownerArea)
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

        if (!ReferenceEquals(runtime.GetPlayer(owner.PlayerId), owner) || attack.AttackerPlayerId != owner.PlayerId)
        {
            throw new InvalidOperationException("Sun orb attack owner must belong to the match and match the attack.");
        }
        if (owner.Position == null || !MatchOrbAttackService.IsSunCrossfireWeapon(attack.WeaponItemId))
        {
            return false;
        }

        var monsterTargets = runtime.Monsters.GetCombatTargets();
        var origin = attack.Origin ?? owner.Position;
        var anchor = attack.AnchorPosition;
        var anchorMonster = runtime.Monsters.FindAliveByCombatTarget(attack.TargetPlayerId);
        int anchorMonsterId = anchorMonster?.MonsterId ?? 0;
        if (anchor == null && runtime.GetPlayer(attack.TargetPlayerId)?.Position is { } targetPosition)
        {
            anchor = targetPosition;
        }

        if (anchor == null && anchorMonster != null && anchorMonster.Health > anchorMonster.PendingDamage)
        {
            anchor = anchorMonster.Position;
        }

        if (anchor == null)
        {
            return false;
        }

        OrbData.TryGetColorAndTier(attack.WeaponItemId, out _, out int tier);
        if (MatchOrbAttackService.CountTelegraphing(runtime.SunCrossfireShapes, owner.PlayerId, nowUtc) >= Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER)
        {
            return false;
        }

        float targetGroundX = anchor.X - origin.X;
        float targetGroundY = (anchor.Y - origin.Y) * GroundGeometry.GroundYScale;
        if (targetGroundX * targetGroundX + targetGroundY * targetGroundY < 0.0025f)
        {
            return false;
        }

        int tierIndex = Math.Clamp(tier, 1, 3) - 1;
        float width = Config.SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER[tierIndex];
        float halfWidth = width * 0.5f;
        float blastRadius = Config.SWARM_CROSSFIRE_SUN_BLAST_RADIUS_BY_TIER[tierIndex];
        float sweepSpeed = Config.SWARM_CROSSFIRE_SUN_SWEEP_SPEED;
        float maxGroundLength = Config.SWARM_CROSSFIRE_SUN_MAX_GROUND_LENGTH;

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
        float reach = halfWidth + GroundGeometry.MonsterRadius;
        float bodyStart = -Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y;
        float bodyEnd = GroundGeometry.MonsterBodyHeight - Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y;
        for (int direction = 0; direction < 4; direction++)
        {
            float candidateLength = maxGroundLength;
            for (float along = AreaBoundaryProbeStep; along < maxGroundLength;
                 along += AreaBoundaryProbeStep)
            {
                var probe = new Vector3f(
                    origin.X + directionX[direction] * along,
                    origin.Y + directionY[direction] * along / GroundGeometry.GroundYScale,
                    0f);
                var cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, probe);
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
                    float relativeY = (monsterTarget.Position.Y + bodyY - origin.Y) * GroundGeometry.GroundYScale;
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
            bool isBetterTargetDirection = false;
            if (hits > 0)
            {
                if (!hasTargetedDirection)
                {
                    isBetterTargetDirection = true;
                }
                else if (nearest < nearestTargetAlong - 0.001f)
                {
                    isBetterTargetDirection = true;
                }
                else if (MathF.Abs(nearest - nearestTargetAlong) <= 0.001f && hits > bestTargetCount)
                {
                    isBetterTargetDirection = true;
                }
            }
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

        bool detonateAtWall = selectedLength < maxGroundLength - 0.01f;
        var endPosition = new Vector3f(origin.X + selectedDirectionX * selectedLength, origin.Y + selectedDirectionY * selectedLength / GroundGeometry.GroundYScale, 0f);

        float sweepSeconds = (selectedLength + width) / sweepSpeed;
        long eventId = MatchOrbAttackService.AllocateEventId();
        var telegraphEndsAtUtc = nowUtc.AddSeconds(Config.SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS);
        runtime.SunCrossfireShapes.Add(new SwarmCrossfireShape
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
            if (session is { PlayerId: not null, Player.IsEliminated: false } && session.Player.GameInfo.ObjectInfo.Area == attack.Area)
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

        if (!ReferenceEquals(runtime.GetPlayer(owner.PlayerId), owner))
        {
            throw new InvalidOperationException("Wind orb attack owner must belong to the match.");
        }

        var ownerArea = owner.GameInfo.ObjectInfo.Area;
        List<Player>? alivePlayers = null;
        List<GameClientSession>? activeSessions = null;
        IReadOnlyList<Monster>? monsterTargets = null;
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

            if (!owner.Orbs.TryBeginWindOrbAttack(orb.ItemUid, nowUtc, Config.SWARM_WIND_BLADE_TICK_SECONDS))
            {
                continue;
            }

            orbTiers ??= orbTrails.GetOrbTiersInOrder(runtime, owner);
            var orbPosition = orbTrails.GetOrbPosition(runtime, owner, ordinal, owner.Position!, orbTiers);
            float radius = Config.SWARM_WIND_BLADE_RADIUS_BY_TIER[Math.Clamp(tier, 1, 3) - 1];
            monsterTargets ??= runtime.Monsters.GetCombatTargets();
            List<Monster>? monstersInRadius = null;
            foreach (var monster in monsterTargets)
            {
                if (monster.Area != ownerArea)
                {
                    continue;
                }

                if (!GroundGeometry.IsWithinGroundRadius(orbPosition, monster.Position, radius + GroundGeometry.MonsterRadius))
                {
                    continue;
                }
                monstersInRadius ??= [];
                monstersInRadius.Add(monster);
            }

            List<Player>? playersInRadius = null;
            alivePlayers ??= runtime.GetAlivePlayers();
            foreach (var participant in alivePlayers)
            {
                if (participant.Position == null)
                {
                    continue;
                }

                if (participant.PlayerId == owner.PlayerId || participant.GameInfo.ObjectInfo.Area != ownerArea)
                {
                    continue;
                }

                if (!GroundGeometry.IsWithinGroundRadius(orbPosition, participant.Position!, radius + GroundGeometry.PlayerRadius))
                {
                    continue;
                }
                playersInRadius ??= [];
                playersInRadius.Add(participant);
            }

            if (monstersInRadius == null && playersInRadius == null)
            {
                owner.Orbs.ResetWindOrbEngagement(orb.ItemUid);
                continue;
            }

            if (!owner.Orbs.UpdateWindOrbSpinup(orb.ItemUid, nowUtc, Config.SWARM_WIND_BLADE_SPINUP_SECONDS))
            {
                continue;
            }

            if (sunDamageMultiplier < 0f)
            {
                sunDamageMultiplier = OrbData.GetSunPveAttackMultiplier(orderedOrbs);
            }

            int damage = Math.Max(1, (int)MathF.Round(OrbData.GetSwarmPveAttackDamage(orb.ItemId) * sunDamageMultiplier * Config.SWARM_WIND_BLADE_DAMAGE_MULTIPLIER));
            if (monstersInRadius != null)
            {
                activeSessions ??= runtime.GetSessions().Where(session => !session.IsGameEnded).ToList();
                foreach (var monster in monstersInRadius)
                {
                    int monsterDamage = combatDamage.RollSwarmCriticalDamage(runtime, damage, out bool critical);
                    combatDamage.ApplySwarmMonsterHitNow(runtime, monster.CombatTargetId, monster.MonsterId, owner.PlayerId, orb.ItemId, ownerArea, monsterDamage, critical, nowUtc, activeSessions);
                }
            }

            if (playersInRadius != null)
            {
                foreach (var participant in playersInRadius)
                {
                    if (!participant.StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc, SwarmWindBladeVictimImmuneSeconds))
                    {
                        continue;
                    }

                    combatDamage.ApplySwarmShock(runtime, healthService, owner.PlayerId, orb.ItemId, ownerArea, participant.PlayerId, alivePlayers);
                    if (runtime.IsEnded)
                    {
                        return;
                    }

                    participant.StatusEffects.Apply(PlayerStatusEffectKind.Wound, nowUtc.AddSeconds(Config.SWARM_WIND_WOUND_SECONDS));
                    var victimSession = participant.Session;
                    if (victimSession is not { PlayerId: not null })
                    {
                        continue;
                    }

                    using var packet = PacketMaker.G_TO_C_STATUS_EFFECT(new()
                    {
                        SourcePlayerId = owner.PlayerId,
                        TargetPlayerId = participant.PlayerId,
                        AreaType = ownerArea,
                        Effect = CombatStatusEffectKind.WindOrbWound,
                        DurationMs = (int)(Config.SWARM_WIND_WOUND_SECONDS * 1000f)
                    });
                    victimSession.TrySend(packet);
                }
            }

        }
    }
}
