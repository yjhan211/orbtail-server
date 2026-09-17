using game_server.matches;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     매치 잠금 안에서 플레이어가 보유한 오브의 공격 발동을 처리한다.
///     발동 시각은 PlayerOrbState가, 지속 중인 공격은 MatchRuntime이 소유한다.
/// </summary>
internal sealed class PlayerOrbService(
    MatchOrbAttackService orbAttacks,
    PlayerOrbTrailService orbTrails)
{
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
            if (!owner.Orbs.IsOrbAttackReady(orb.ItemUid, nowUtc))
            {
                continue;
            }

            var orbPosition = orbTrails.GetOrbPosition(runtime, owner, ordinal, owner.Position, orbTiers);
            bool consumeAttackInterval = orbGroupId switch
            {
                OrbGroupIds.Wind => ActivateWindOrb(runtime, owner, orb, tier, orbPosition, attackMultiplier, nowUtc),
                OrbGroupIds.Wave => ActivateWaveOrb(runtime, owner, orb, orbPosition, attackMultiplier, appliesSlow, nowUtc),
                OrbGroupIds.Sun => ActivateSunOrb(runtime, owner, orb, ordinal, tier, orbPosition, attackMultiplier, nowUtc),
                _ => false
            };
            if (consumeAttackInterval)
            {
                owner.Orbs.ScheduleNextOrbAttack(orb.ItemUid, nowUtc);
            }
        }
    }

    private bool ActivateSunOrb(MatchRuntime runtime, Player owner, InGameItemInfo orb, int ordinal, int tier, Vector3f origin, float attackMultiplier, DateTime nowUtc)
    {
        var ownerArea = owner.GameInfo.ObjectInfo.Area;
        float range = Config.TierValue(Config.SWARM_CROSSFIRE_SUN_RANGE_BY_TIER, tier);
        var (monsters, players) = MatchOrbAttackService.CollectTargetsInRadius(runtime, owner.PlayerId, ownerArea, origin, range, padBodyRadius: false);

        // 이미 겨눈 표적은 제외
        var anchoredTargets = new HashSet<(ObjectType Type, long Id)>();
        foreach (var shape in runtime.SunCrossfireShapes)
        {
            if (shape.OwnerId == owner.PlayerId)
            {
                anchoredTargets.Add(shape.AnchorTarget);
            }
        }

        var candidates = new List<(ObjectType Type, long Id, Vector3f Position)>(players.Count + monsters.Count);
        foreach (var player in players)
        {
            if (!anchoredTargets.Contains((ObjectType.PLAYER, player.PlayerId)))
            {
                candidates.Add((ObjectType.PLAYER, player.PlayerId, player.Position!));
            }
        }
        foreach (var monster in monsters)
        {
            if (!anchoredTargets.Contains((ObjectType.MONSTER, monster.MonsterId)))
            {
                candidates.Add((ObjectType.MONSTER, monster.MonsterId, monster.Position));
            }
        }

        int bestPriority = int.MaxValue;
        float nearestDistance = float.MaxValue;
        (ObjectType Type, long Id) target = default;
        Vector3f? anchor = null;
        foreach (var candidate in candidates)
        {
            int priority = candidate.Type == ObjectType.PLAYER ? 0 : 1;
            float distance = GroundGeometry.GroundDistance(origin, candidate.Position);
            if (priority > bestPriority)
            {
                continue;
            }
            if (priority == bestPriority)
            {
                if (distance > nearestDistance)
                {
                    continue;
                }
                if (distance.Equals(nearestDistance) && candidate.Id >= target.Id)
                {
                    continue;
                }
            }
            bestPriority = priority;
            nearestDistance = distance;
            target = (candidate.Type, candidate.Id);
            anchor = candidate.Position;
        }
        if (anchor == null)
        {
            return false;
        }

        var mapId = Config.SWARM_MATCH_MAP;
        var originCell = MapCoordinateConverter.WorldToCell(mapId, origin);
        var targetCell = MapCoordinateConverter.WorldToCell(mapId, anchor);
        origin = MapCoordinateConverter.CellToWorld(mapId, originCell);
        int dx = targetCell.X - originCell.X;
        int dy = targetCell.Y - originCell.Y;

        // 셀 차이가 더 큰 축으로 발사
        int stepX = 0;
        int stepY = 0;
        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            stepX = dx >= 0 ? 1 : -1;
        }
        else
        {
            stepY = dy >= 0 ? 1 : -1;
        }

        var nextCell = new Cell(originCell.X + stepX, originCell.Y + stepY);
        var nextPosition = MapCoordinateConverter.CellToWorld(mapId, nextCell);
        float groundLengthPerCell = GroundGeometry.GroundDistance(origin, nextPosition);
        float maxGroundLength = Config.SWARM_CROSSFIRE_SUN_MAX_GROUND_LENGTH;
        int maxCellSteps = (int)MathF.Floor(maxGroundLength / groundLengthPerCell);
        var endCell = originCell;
        bool detonateAtWall = false;

        // 최대 사거리 안에서 같은 구역의 마지막 셀 중심까지 발사한다.
        for (int step = 1; step <= maxCellSteps; step++)
        {
            int cellX = originCell.X + stepX * step;
            int cellY = originCell.Y + stepY * step;
            if (GameMapData.GetCurrentArea(mapId, cellX, cellY) != ownerArea)
            {
                detonateAtWall = true; // 벽에 부딪히면 폭발
                break;
            }
            endCell = new Cell(cellX, cellY);
        }
        var endPosition = MapCoordinateConverter.CellToWorld(mapId, endCell);
        float selectedLength = GroundGeometry.GroundDistance(origin, endPosition);
        if (selectedLength < 0.5)
        {
            // 발사 거리가 반 칸 이내면 생략
            return false;
        }

        float width = Config.TierValue(Config.SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER, tier);
        int damage = OrbData.GetAttackDamage(orb.ItemId, attackMultiplier, Config.SWARM_CROSSFIRE_SUN_DAMAGE_MULTIPLIER);

        float sweepSeconds = (selectedLength + width) / Config.SWARM_CROSSFIRE_SUN_SWEEP_SPEED;
        long eventId = MatchOrbAttackService.AllocateEventId();
        var armedAtUtc = nowUtc.AddSeconds(Config.SWARM_SUN_ORB_ATTACK_WINDUP_SECONDS);
        runtime.SunCrossfireShapes.Add(new SwarmCrossfireShape
        {
            EventId = eventId,
            OwnerId = owner.PlayerId,
            WeaponItemId = orb.ItemId,
            Damage = damage,
            Area = ownerArea,
            OriginCell = originCell,
            EndCell = endCell,
            ArmedAtUtc = armedAtUtc,
            ExpiresAtUtc = armedAtUtc.AddSeconds(sweepSeconds),
            DetonateAtEnd = detonateAtWall,
            OwnerOrbOrdinal = ordinal,
            AnchorTarget = target
        });

        return true;
    }

    /// <summary>
    ///     범위 안에 대상이 있으면 파도 공격의 위치·피해량·기폭 시각을 저장한다.
    ///     실제 피격 대상은 기폭 시 다시 판정하며, 대상이 없으면 공격 주기를 소비하지 않는다.
    /// </summary>
    private bool ActivateWaveOrb(MatchRuntime runtime, Player owner, InGameItemInfo orb, Vector3f orbPosition, float attackMultiplier, bool appliesSlow, DateTime nowUtc)
    {
        var ownerArea = owner.GameInfo.ObjectInfo.Area;
        float radius = OrbData.GetWaveVortexRadius(orb.ItemId);
        if (radius <= 0f)
        {
            return false;
        }
        if (!MatchOrbAttackService.HasTargetInRadius(runtime, owner.PlayerId, ownerArea, orbPosition, radius))
        {
            return false;
        }
        int damage = OrbData.GetAttackDamage(orb.ItemId, attackMultiplier, Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER);
        var detonatesAtUtc = nowUtc.AddSeconds(Config.SWARM_WAVE_VORTEX_FUSE_SECONDS);
        runtime.PendingWaveAttacks.Add(new PendingWaveAttack(owner.PlayerId, ownerArea, orbPosition, damage, radius, orb.ItemId, detonatesAtUtc, appliesSlow));
        return true;
    }

    /// <summary>
    ///     바람 공격의 범위와 피해량을 계산하고 즉시 공격 처리를 호출한다.
    ///     대상 유무와 관계없이 공격 주기를 소비한다.
    /// </summary>
    private bool ActivateWindOrb(MatchRuntime runtime, Player owner, InGameItemInfo orb, int tier, Vector3f orbPosition, float attackMultiplier, DateTime nowUtc)
    {
        float radius = Config.TierValue(Config.SWARM_WIND_BLADE_RADIUS_BY_TIER, tier);
        int damage = OrbData.GetAttackDamage(orb.ItemId, attackMultiplier, Config.SWARM_WIND_BLADE_DAMAGE_MULTIPLIER);
        orbAttacks.ProcessWindAttack(runtime, owner, orb.ItemId, orbPosition, radius, damage, nowUtc);
        return true;
    }
}
