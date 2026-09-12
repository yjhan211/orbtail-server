using game_server.matches.combat;
using game_server.players;
using network.common;
using network.common.data;

namespace game_server.matches;

/// <summary>
///     매치의 플레이어 본체·보유 오브·몬스터를 자동공격 계산용 데이터로 구성한다.
///     각 개체의 위치와 공격 능력치를 반영하며, 매치 상태는 변경하지 않는다.
/// </summary>
internal sealed class MatchCombatActorBuilder(PlayerOrbTrailService orbTrails)
{
    private static float SwarmOrbCadenceJitterRatio => SwarmConfigData.GetFloat("SWARM_ORB_CADENCE_JITTER_RATIO", 0.12f);

    public static void AddOrbActors(ICollection<ProximityCombatActor> actors, ProximityCombatActor bodyActor, PlayerOrbCollection orbs)
    {
        foreach (var orb in orbs.GetAllItems().Where(item => item.Count > 0).OrderBy(item => item.ItemUid))
        {
            bool attackOrb = OrbData.TryGetColorAndTier(orb.ItemId, out _, out _) && BattleItemCombatData.Get(orb.ItemId) != null;
            if (!OrbData.IsRecoveryOrb(orb.ItemId) && !attackOrb)
            {
                continue;
            }

            for (int stackIndex = 0; stackIndex < orb.Count; stackIndex++)
            {
                actors.Add(bodyActor with
                {
                    WeaponItemId = orb.ItemId,
                    WeaponItemUid = orb.ItemUid,
                    WeaponStackIndex = stackIndex
                });
            }
        }
    }

    public List<ProximityCombatActor> Build(MatchRuntime runtime, IReadOnlyList<Player> players, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat actors require the match lock.");
        }

        var actors = new List<ProximityCombatActor>();
        long nowUnixMs = (long)(nowUtc - DateTime.UnixEpoch).TotalMilliseconds;
        foreach (var player in players)
        {
            if (player.IsEliminated || player.Position == null || player.CurrentArea == AreaType.None)
            {
                continue;
            }

            var cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, player.Position);
            var cellArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell);
            if (cellArea != player.CurrentArea || !GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell))
            {
                continue;
            }

            var spatial = new ProximityCombatActor(player.PlayerId, cellArea, player.Position, 0, 0f, 0, 0f, MapId: Config.SWARM_MATCH_MAP, Cell: cell);
            var body = spatial with { WeaponItemUid = spatial.PlayerId };
            actors.Add(body);

            var orbCollection = runtime.GetOrbs(player.PlayerId);
            int firstOrbIndex = actors.Count;
            AddOrbActors(actors, spatial, orbCollection);
            int orbCount = actors.Count - firstOrbIndex;
            if (orbCount == 0)
            {
                continue;
            }

            float sunAttackMultiplier = OrbData.GetSunPveAttackMultiplier(orbCollection.GetAllItems());
            var orbTiers = orbTrails.GetOrbTiersInOrder(runtime, player);
            for (int ordinal = 0; ordinal < orbCount; ordinal++)
            {
                int index = firstOrbIndex + ordinal;
                var actor = actors[index];
                var trailPosition = orbTrails.GetOrbPosition(runtime, player, ordinal, spatial.Position, orbTiers);
                actor = actor with
                {
                    Position = trailPosition,
                    Cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, trailPosition),
                    TrailOrdinal = ordinal,
                    Untargetable = true
                };
                bool attackOrb = OrbData.TryGetColorAndTier(actor.WeaponItemId, out var orbColor, out int orbTier);
                if (orbColor is OrbColor.Blue or OrbColor.Green)
                {
                    actors[index] = actor;
                    continue;
                }

                var combatData = BattleItemCombatData.Get(actor.WeaponItemId);
                int baseDamage = attackOrb ? combatData?.Damage ?? 0 : 0;
                float baseIntervalSeconds = attackOrb ? combatData?.AttackIntervalSeconds ?? 0f : 0f;

                bool crossfireSun = MatchOrbAttackService.IsSunCrossfireWeapon(actor.WeaponItemId);
                float crossfireDamageMultiplier = crossfireSun ? Config.SWARM_CROSSFIRE_SUN_DAMAGE_MULTIPLIER : 1f;
                float crossfireCadenceMultiplier = crossfireSun ? Config.SWARM_CROSSFIRE_SUN_CADENCE_MULTIPLIER : 1f;
                float attackRange = crossfireSun ? Config.SWARM_CROSSFIRE_SUN_RANGE_BY_TIER[Math.Clamp(orbTier, 1, 3) - 1] : Config.SWARM_PVE_SAME_AREA_ATTACK_RANGE;

                int damage = Math.Max(1, (int)MathF.Round(baseDamage * sunAttackMultiplier * crossfireDamageMultiplier));
                float attackIntervalSeconds = baseIntervalSeconds * ResolveSwarmOrbCadenceJitter(ordinal) * crossfireCadenceMultiplier;
                actors[index] = actor with
                {
                    Damage = damage,
                    AttackIntervalSeconds = attackIntervalSeconds,
                    AttackRange = attackRange
                };
            }

            if (orbCount > 1)
            {
                int rotation = (int)(nowUnixMs / 50 % orbCount);
                if (rotation > 0)
                {
                    var rotated = new ProximityCombatActor[orbCount];
                    for (int offset = 0; offset < orbCount; offset++)
                    {
                        rotated[offset] = actors[firstOrbIndex + (offset + rotation) % orbCount];
                    }
                    for (int offset = 0; offset < orbCount; offset++)
                    {
                        actors[firstOrbIndex + offset] = rotated[offset];
                    }
                }
            }
        }

        foreach (var target in runtime.Monsters.GetCombatTargets())
        {
            actors.Add(new ProximityCombatActor(
                target.CombatTargetId,
                target.Area,
                target.Position,
                0,
                0f,
                0,
                0f,
                MapId: Config.SWARM_MATCH_MAP,
                Cell: MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target.Position),
                IsMonsterTarget: true,
                TargetPriority: 2));
        }

        return actors;
    }

    private static float ResolveSwarmOrbCadenceJitter(int trailOrdinal)
    {
        float phase = trailOrdinal * 0.6180339f;
        phase -= MathF.Floor(phase);
        float ratio = SwarmOrbCadenceJitterRatio;
        return 1f - ratio + phase * 2f * ratio;
    }
}
