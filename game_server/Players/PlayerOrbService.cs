using game_server.matches;
using game_server.matches.monsters;
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

    public void ActivateOrbs(MatchRuntime runtime, Player owner, DateTime nowUtc)
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

        var orderedOrbs = owner.Orbs.GetOrderedOrbs();
        if (orderedOrbs.Count == 0)
        {
            return;
        }

        float attackMultiplier = OrbData.GetAttackMultiplier(orderedOrbs);
        bool appliesSlow = OrbData.IsResonating(orderedOrbs, OrbGroupIds.Wave);
        var orbTiers = orderedOrbs.Select(orb => PlayerOrbState.GetOrbTier(orb.ItemId)).ToList();
        for (int ordinal = 0; ordinal < orderedOrbs.Count; ordinal++)
        {
            if (runtime.IsEnded)
            {
                return;
            }
            var orb = orderedOrbs[ordinal];
            if (!OrbData.TryGetOrbGroupAndTier(orb.ItemId, out int orbGroupId, out int tier))
            {
                continue;
            }
            switch (orbGroupId)
            {
                case OrbGroupIds.Wind:
                    if (!owner.Orbs.IsOrbAttackReady(orb.ItemUid, nowUtc))
                    {
                        continue;
                    }
                    owner.Orbs.ScheduleNextOrbAttack(orb.ItemUid, nowUtc);
                    var windPosition = orbTrails.GetOrbPosition(runtime, owner, ordinal, owner.Position, orbTiers);
                    ActivateWindOrb(runtime, owner, orb, tier, windPosition, attackMultiplier, nowUtc);
                    break;

                case OrbGroupIds.Wave:
                    if (!owner.Orbs.IsOrbAttackReady(orb.ItemUid, nowUtc))
                    {
                        continue;
                    }
                    var wavePosition = orbTrails.GetOrbPosition(runtime, owner, ordinal, owner.Position, orbTiers);
                    ActivateWaveOrb(runtime, owner, orb, ordinal, wavePosition, attackMultiplier, appliesSlow, nowUtc);
                    break;

                case OrbGroupIds.Sun:
                    if (!owner.Orbs.IsOrbAttackReady(orb.ItemUid, nowUtc))
                    {
                        continue;
                    }
                    var sunPosition = orbTrails.GetOrbPosition(runtime, owner, ordinal, owner.Position, orbTiers);
                    if (ActivateSunOrb(runtime, owner, orb, ordinal, tier, sunPosition, attackMultiplier, nowUtc))
                    {
                        owner.Orbs.ScheduleNextOrbAttack(orb.ItemUid, nowUtc);
                    }
                    break;
            }
        }
    }

    private bool ActivateSunOrb(MatchRuntime runtime, Player owner, InGameItemInfo orb, int ordinal, int tier, Vector3f origin, float attackMultiplier, DateTime nowUtc)
    {
        var ownerArea = owner.GameInfo.ObjectInfo.Area;
        float range = Config.SWARM_CROSSFIRE_SUN_RANGE_BY_TIER[Math.Clamp(tier, 1, 3) - 1];
        long targetId = 0;
        Vector3f? anchor = null;
        float nearestSquared = float.MaxValue;
        foreach (var participant in runtime.GetAlivePlayers())
        {
            if (participant.PlayerId == owner.PlayerId || participant.Position == null || participant.GameInfo.ObjectInfo.Area != ownerArea || IsSunAnchored(runtime, owner.PlayerId, participant.PlayerId))
            {
                continue;
            }
            if (!GroundGeometry.IsWithinGroundRadius(origin, participant.Position, range))
            {
                continue;
            }
            float dx = participant.Position.X - origin.X;
            float dy = participant.Position.Y - origin.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < nearestSquared || distanceSquared.Equals(nearestSquared) && participant.PlayerId < targetId)
            {
                nearestSquared = distanceSquared;
                targetId = participant.PlayerId;
                anchor = participant.Position;
            }
        }
        if (anchor == null)
        {
            foreach (var monster in runtime.Monsters.GetCombatTargets())
            {
                if (monster.Area != ownerArea || IsSunAnchored(runtime, owner.PlayerId, monster.CombatTargetId))
                {
                    continue;
                }
                if (!GroundGeometry.IsWithinGroundRadius(origin, monster.Position, range))
                {
                    continue;
                }
                float dx = monster.Position.X - origin.X;
                float dy = monster.Position.Y - origin.Y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared < nearestSquared || distanceSquared.Equals(nearestSquared) && monster.CombatTargetId < targetId)
                {
                    nearestSquared = distanceSquared;
                    targetId = monster.CombatTargetId;
                    anchor = monster.Position;
                }
            }
        }
        if (anchor == null)
        {
            return false;
        }

        int damage = Math.Max(1, (int)MathF.Round(OrbData.GetAttackDamage(orb.ItemId) * attackMultiplier * Config.SWARM_CROSSFIRE_SUN_DAMAGE_MULTIPLIER));
        return TryStartSunCrossfire(runtime, owner, orb, ordinal, tier, origin, targetId, anchor, damage, nowUtc);
    }

    /// <summary>이 소유자의 태양 도형이 이미 겨눈 표적인지 — 같은 표적에 두 발을 겹치지 않는다.</summary>
    private static bool IsSunAnchored(MatchRuntime runtime, long ownerId, long combatTargetId)
    {
        foreach (var shape in runtime.SunCrossfireShapes)
        {
            if (shape.OwnerId == ownerId && shape.AnchorCombatTargetId == combatTargetId)
            {
                return true;
            }
        }
        return false;
    }

    private void ActivateWaveOrb(MatchRuntime runtime, Player owner, InGameItemInfo orb, int ordinal, Vector3f orbPosition, float attackMultiplier, bool appliesSlow, DateTime nowUtc)
    {
        var ownerArea = owner.GameInfo.ObjectInfo.Area;
        float radius = OrbData.GetWaveVortexRadius(orb.ItemId);
        if (radius <= 0f)
        {
            return;
        }

        var monsterTargets = runtime.Monsters.GetCombatTargets();
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
            var alivePlayers = runtime.GetAlivePlayers();
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
            return;
        }

        owner.Orbs.ScheduleNextOrbAttack(orb.ItemUid, nowUtc);

        int damage = Math.Max(1, (int)MathF.Round(OrbData.GetAttackDamage(orb.ItemId) * attackMultiplier * Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER));
        runtime.PendingWaveAttacks.Add(new PendingWaveAttack(owner.PlayerId, ownerArea, orbPosition, damage, radius, orb.ItemId, nowUtc.AddSeconds(Config.SWARM_WAVE_VORTEX_FUSE_SECONDS), appliesSlow));
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

    private bool TryStartSunCrossfire(MatchRuntime runtime, Player owner, InGameItemInfo orb, int ordinal, int tier, Vector3f origin, long targetId, Vector3f anchor, int damage, DateTime nowUtc)
    {
        var ownerArea = owner.GameInfo.ObjectInfo.Area;
        var monsterTargets = runtime.Monsters.GetCombatTargets();
        int anchorMonsterId = runtime.Monsters.FindAliveByCombatTarget(targetId)?.MonsterId ?? 0;
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
                if (GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell) == ownerArea)
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
                if (monsterTarget.Area != ownerArea)
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
            OwnerId = owner.PlayerId,
            WeaponItemId = orb.ItemId,
            Damage = damage,
            Area = ownerArea,
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
            AnchorCombatTargetId = targetId,
            LastFront = -halfWidth
        });

        using var packet = Packet.Create((int)Protocol.G_TO_C_SUN_ORB_ATTACK);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUN_ORB_ATTACK
        {
            EventId = eventId,
            OwnerPlayerId = owner.PlayerId,
            WeaponItemId = orb.ItemId,
            Shape = Config.SWARM_CROSSFIRE_SHAPE_LINE,
            OriginX = origin.X,
            OriginY = origin.Y,
            EndX = endPosition.X,
            EndY = endPosition.Y,
            Width = width,
            TelegraphSeconds = Config.SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS,
            ActiveSeconds = sweepSeconds,
            AnchorMonsterId = anchorMonsterId,
            OwnerOrbOrdinal = ordinal
        }));
        foreach (var session in runtime.GetSessions())
        {
            if (session is { PlayerId: not null, Player.IsEliminated: false } && session.Player.GameInfo.ObjectInfo.Area == ownerArea)
            {
                session.TrySend(packet);
            }
        }
        return true;
    }

    private void ActivateWindOrb(MatchRuntime runtime, Player owner, InGameItemInfo orb, int tier,
        Vector3f orbPosition, float attackMultiplier, DateTime nowUtc)
    {
        var ownerArea = owner.GameInfo.ObjectInfo.Area;
        float radius = Config.SWARM_WIND_BLADE_RADIUS_BY_TIER[Math.Clamp(tier, 1, 3) - 1];
        var monsterTargets = runtime.Monsters.GetCombatTargets();
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
        var alivePlayers = runtime.GetAlivePlayers();
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
            return;
        }


        int damage = Math.Max(1, (int)MathF.Round(OrbData.GetAttackDamage(orb.ItemId) * attackMultiplier * Config.SWARM_WIND_BLADE_DAMAGE_MULTIPLIER));
        if (monstersInRadius != null)
        {
            var activeSessions = runtime.GetSessions().Where(session => !session.IsGameEnded).ToList();
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
                if (!participant.StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc, Config.SWARM_WIND_BLADE_VICTIM_IMMUNE_SECONDS))
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
