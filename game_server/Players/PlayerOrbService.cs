using game_server.matches;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     매치 잠금 안에서 플레이어가 보유한 오브의 공격 발동을 처리한다.
///     발동 시각은 PlayerOrbState가, 지속 중인 공격은 MatchRuntime이 소유한다.
/// </summary>
internal sealed class PlayerOrbService(PlayerOrbTrailService orbTrails)
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
        var orbTiers = PlayerOrbTrailService.GetOrbTiersInOrder(runtime, owner);
        for (int ordinal = 0; ordinal < orderedOrbs.Count; ordinal++)
        {
            var orb = orderedOrbs[ordinal];
            if (!OrbData.TryGetOrbGroupAndTier(orb.ItemId, out int orbGroupId, out int tier))
            {
                continue;
            }
            if (!owner.Orbs.IsOrbAttackReady(orb.ItemUid, nowUtc))
            {
                continue;
            }

            // 발동하지 못한 오브는 공격 주기를 쓰지 않고 다음 틱에 다시 시도
            var orbPosition = orbTrails.GetOrbPosition(runtime, owner, ordinal, owner.Position, orbTiers);
            bool activated = orbGroupId switch
            {
                OrbGroupIds.Wind => ActivateWindOrb(runtime, owner, orb, tier, orbPosition, attackMultiplier),
                OrbGroupIds.Wave => ActivateWaveOrb(runtime, owner, orb, orbPosition, attackMultiplier, appliesSlow, nowUtc),
                OrbGroupIds.Sun => ActivateSunOrb(runtime, owner, orb, ordinal, tier, orbPosition, attackMultiplier, nowUtc),
                _ => false
            };
            if (activated)
            {
                owner.Orbs.ScheduleNextOrbAttack(orb.ItemUid, nowUtc);
            }
        }
    }

    private bool ActivateSunOrb(MatchRuntime runtime, Player owner, InGameItemInfo orb, int ordinal, int tier, Vector3f origin, float attackMultiplier, DateTime nowUtc)
    {
        var ownerArea = owner.CurrentArea;
        float range = Config.TierValue(Config.SWARM_SUN_RANGE_BY_TIER, tier);
        if (!TrySelectSunTarget(runtime, owner, ownerArea, origin, range, out var target, out var anchor) || anchor == null)
        {
            return false;
        }

        // 셀 차이가 더 큰 축으로 발사
        var mapId = Config.SWARM_MATCH_MAP;
        var originCell = MapCoordinateConverter.WorldToCell(mapId, origin);
        var targetCell = MapCoordinateConverter.WorldToCell(mapId, anchor);
        origin = MapCoordinateConverter.CellToWorld(mapId, originCell);
        int dx = targetCell.X - originCell.X;
        int dy = targetCell.Y - originCell.Y;
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

        // 최대 사거리 안에서 같은 구역의 마지막 셀 중심까지
        var nextPosition = MapCoordinateConverter.CellToWorld(mapId, new Cell(originCell.X + stepX, originCell.Y + stepY));
        float groundLengthPerCell = GroundGeometry.GroundDistance(origin, nextPosition);
        int maxCellSteps = (int)MathF.Floor(Config.SWARM_SUN_MAX_GROUND_LENGTH / groundLengthPerCell);
        var endCell = originCell;
        bool detonateAtWall = false;
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

        float length = GroundGeometry.GroundDistance(origin, MapCoordinateConverter.CellToWorld(mapId, endCell));
        if (length < 0.5f)
        {
            return false;
        }

        float sweepSeconds = (length + OrbData.GetSunWidth(orb.ItemId)) / Config.SWARM_SUN_SWEEP_SPEED;
        var armedAtUtc = nowUtc.AddSeconds(Config.SWARM_SUN_ORB_ATTACK_WINDUP_SECONDS);
        runtime.PendingSunAttacks.Add(new PendingSunAttack
        {
            EventId = PendingSunAttack.AllocateEventId(),
            OwnerId = owner.PlayerId,
            WeaponItemId = orb.ItemId,
            Damage = OrbData.GetAttackDamage(orb.ItemId, attackMultiplier, Config.SWARM_SUN_DAMAGE_MULTIPLIER),
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

    private static bool TrySelectSunTarget(MatchRuntime runtime, Player owner, AreaType ownerArea, Vector3f origin, float range, out (ObjectType Type, long Id) target, out Vector3f? anchor)
    {
        target = default;
        anchor = null;
        var anchoredTargets = new HashSet<(ObjectType Type, long Id)>();
        foreach (var shape in runtime.PendingSunAttacks)
        {
            if (shape.OwnerId == owner.PlayerId)
            {
                anchoredTargets.Add(shape.AnchorTarget);
            }
        }

        var (monsters, players) = MatchOrbTarget.CollectTargetsInRadius(runtime, owner.PlayerId, ownerArea, origin, range);
        float nearestDistance = float.MaxValue;
        foreach (var player in players)
        {
            if (anchoredTargets.Contains((ObjectType.PLAYER, player.PlayerId)))
            {
                continue;
            }
            float distance = GroundGeometry.GroundDistance(origin, player.Position!);
            if (distance > nearestDistance || (distance.Equals(nearestDistance) && player.PlayerId >= target.Id))
            {
                continue;
            }
            nearestDistance = distance;
            target = (ObjectType.PLAYER, player.PlayerId);
            anchor = player.Position;
        }
        if (anchor != null)
        {
            return true;
        }

        foreach (var monster in monsters)
        {
            if (anchoredTargets.Contains((ObjectType.MONSTER, monster.MonsterId)))
            {
                continue;
            }
            float distance = GroundGeometry.GroundDistance(origin, monster.Position);
            if (distance > nearestDistance || (distance.Equals(nearestDistance) && monster.MonsterId >= target.Id))
            {
                continue;
            }
            nearestDistance = distance;
            target = (ObjectType.MONSTER, monster.MonsterId);
            anchor = monster.Position;
        }
        return anchor != null;
    }

    private bool ActivateWaveOrb(MatchRuntime runtime, Player owner, InGameItemInfo orb, Vector3f orbPosition, float attackMultiplier, bool appliesSlow, DateTime nowUtc)
    {
        var ownerArea = owner.CurrentArea;
        float radius = OrbData.GetWaveVortexRadius(orb.ItemId);
        if (radius <= 0f)
        {
            return false;
        }
        var (monsters, players) = MatchOrbTarget.CollectTargetsInRadius(runtime, owner.PlayerId, ownerArea, orbPosition, radius);
        if (monsters.Count == 0 && players.Count == 0)
        {
            return false;
        }
        int damage = OrbData.GetAttackDamage(orb.ItemId, attackMultiplier, Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER);
        var detonatesAtUtc = nowUtc.AddSeconds(Config.SWARM_WAVE_VORTEX_FUSE_SECONDS);
        runtime.PendingWaveAttacks.Add(new PendingWaveAttack(owner.PlayerId, ownerArea, orbPosition, damage, radius, orb.ItemId, detonatesAtUtc, appliesSlow));
        return true;
    }

    private bool ActivateWindOrb(MatchRuntime runtime, Player owner, InGameItemInfo orb, int tier, Vector3f orbPosition, float attackMultiplier)
    {
        float radius = Config.TierValue(Config.SWARM_WIND_BLADE_RADIUS_BY_TIER, tier);
        int damage = OrbData.GetAttackDamage(orb.ItemId, attackMultiplier, Config.SWARM_WIND_BLADE_DAMAGE_MULTIPLIER);
        runtime.PendingWindAttacks.Add(new PendingWindAttack(owner.PlayerId, orb.ItemId, orbPosition, radius, damage));
        return true;
    }
}
