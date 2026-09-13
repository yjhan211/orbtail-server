using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>
///     매치의 몬스터 공급·스폰과 공급 보상 예산을 처리한다.
///     상태는 MatchMonsters에 보관하며, 호출자는 매치 잠금을 보유해야 한다.
/// </summary>
internal sealed class MatchMonsterSpawnService(MonsterBehaviorService movement)
{
    private const int CampsPerArea = 3;
    private const int FirstMonsterId = 7_000_000;
    private const long FirstCombatTargetId = -4_000_000_000_000_000_000L;
    private const int AnchorAreaEdgeMargin = 3;
    private static readonly int InsigniaCount = Enum.GetValues<MonsterInsignia>().Length;

    private static int GetSupplyPhaseIndex(double elapsedSeconds)
    {
        var phases = SwarmSupplyPhaseData.GetAll();
        for (int index = 0; index < phases.Count; index++)
        {
            if (elapsedSeconds < phases[index].UntilSeconds)
            {
                return index;
            }
        }
        return phases.Count - 1;
    }

    private static int GetSupplyZoneTarget(int perPlayerTarget, int playersInZone)
    {
        int players = Math.Clamp(playersInZone, 1, Config.SWARM_MONSTER_SUPPLY_ZONE_CROWD_CAP);
        int linearPlayers = Math.Min(players, Config.SWARM_MONSTER_SUPPLY_CROWD_LINEAR_PLAYERS);
        int crowdPlayers = players - linearPlayers;

        return perPlayerTarget * linearPlayers + (int)Math.Round(perPlayerTarget * Config.SWARM_MONSTER_SUPPLY_CROWD_EXTRA_RATIO * crowdPlayers);
    }

    public void ProcessSupply(MatchRuntime runtime, IReadOnlyList<PlayerPositionSnapshot> participants, DateTime now, bool preMatch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster supply requires the match lock.");
        }
        var state = runtime.Monsters;
        double elapsed = (now - state.StartsAtUtc).TotalSeconds;
        state.MaxParticipantCount = Math.Max(state.MaxParticipantCount, participants.Count);
        int phaseIndex = GetSupplyPhaseIndex(elapsed);
        var phase = SwarmSupplyPhaseData.GetAll()[phaseIndex];
        var occupied = new Dictionary<AreaType, List<long>>();
        foreach (var participant in participants)
        {
            if (participant.Area == AreaType.None || runtime.Closures.IsAreaClosed(participant.Area))
            {
                continue;
            }
            if (!occupied.TryGetValue(participant.Area, out var roster))
            {
                roster = [];
                occupied[participant.Area] = roster;
            }
            roster.Add(participant.PlayerId);
        }

        if (preMatch)
        {
            foreach (var room in MatchSpawnData.GetPhaseRoomCandidates())
            {
                if (runtime.Closures.IsAreaClosed(room))
                {
                    continue;
                }
                occupied.TryAdd(room, new List<long>());
            }
        }

        var vacatedZones = new List<AreaType>();
        foreach (var zone in state.SupplyZones.Keys)
        {
            if (!occupied.ContainsKey(zone))
            {
                vacatedZones.Add(zone);
            }
        }
        foreach (var zone in vacatedZones)
        {
            state.SupplyZones.Remove(zone);
        }

        ReclaimStrandedMonsters(runtime, occupied, now);
        int aliveGlobal = 0;
        foreach (var monster in state.Entities.Values)
        {
            if (monster.Alive)
            {
                aliveGlobal++;
            }
        }
        int zoneTargetSum = 0;
        foreach (var roster in occupied.Values)
        {
            zoneTargetSum += GetSupplyZoneTarget(phase.PerPlayerTarget, roster.Count);
        }
        int globalCap = Math.Min(Config.SWARM_MONSTER_SUPPLY_GLOBAL_ALIVE_HARD_CAP, zoneTargetSum);
        var zonesByAlive = new List<(AreaType Zone, List<long> Roster, int Alive, int Order)>(occupied.Count);
        foreach (var (zone, roster) in occupied)
        {
            zonesByAlive.Add((zone, roster, CountAliveInArea(state, zone), zonesByAlive.Count));
        }
        zonesByAlive.Sort(static (left, right) => left.Alive != right.Alive ? left.Alive.CompareTo(right.Alive) : left.Order.CompareTo(right.Order));

        foreach (var (zone, roster, _, _) in zonesByAlive)
        {
            int zoneTarget = GetSupplyZoneTarget(phase.PerPlayerTarget, roster.Count);
            if (aliveGlobal >= globalCap)
            {
                break;
            }

            if (!state.SupplyZones.TryGetValue(zone, out var zoneState))
            {
                zoneState = new MonsterSupplyZoneState();
                state.SupplyZones[zone] = zoneState;
            }

            int aliveInZone = CountAliveInArea(state, zone);
            if (aliveInZone >= zoneTarget)
            {
                zoneState.NextTopUpAtUtc = null;
                continue;
            }

            if (aliveInZone == 0 && zoneState.HasSpawned)
            {
                if (zoneState.WipeRestUntilUtc == null)
                {
                    bool finalPhase = phaseIndex == SwarmSupplyPhaseData.GetAll().Count - 1;
                    zoneState.WipeRestUntilUtc = now.AddSeconds(finalPhase ? Config.SWARM_MONSTER_SUPPLY_WIPE_REST_SECONDS_FINAL_PHASE : Config.SWARM_MONSTER_SUPPLY_WIPE_REST_SECONDS);
                    zoneState.NextTopUpAtUtc = null;
                }

                if (now < zoneState.WipeRestUntilUtc)
                {
                    continue;
                }
            }
            else if (aliveInZone > 0)
            {
                zoneState.WipeRestUntilUtc = null;
            }
            zoneState.NextTopUpAtUtc ??= now;
            if (now < zoneState.NextTopUpAtUtc)
            {
                continue;
            }

            bool hasAliveCore = false;
            foreach (var monster in state.Entities.Values)
            {
                if (monster.Alive && monster.HomeArea == zone && monster.Kind == MonsterKind.RunawayGoblin)
                {
                    hasAliveCore = true;
                    break;
                }
            }
            bool includeCore = phaseIndex >= Config.SWARM_MONSTER_SUPPLY_CORE_FIRST_PHASE_INDEX && !hasAliveCore && aliveInZone < zoneTarget && aliveGlobal < globalCap;
            int room = zoneTarget - aliveInZone - (includeCore ? 1 : 0);
            int want = Math.Min(Config.SWARM_MONSTER_SUPPLY_TOP_UP_COUNT, room);
            want = Math.Min(want, globalCap - aliveGlobal - (includeCore ? 1 : 0));
            if (want <= 0 && !includeCore)
            {
                continue;
            }

            want = Math.Max(0, want);
            int spawned = SpawnSupplyMonsters(runtime, participants, zone, want, includeCore, phaseIndex, now, roster);
            if (spawned == 0)
            {
                zoneState.NextTopUpAtUtc = now.AddSeconds(Config.SWARM_MONSTER_SUPPLY_BLOCKED_RETRY_SECONDS);
                continue;
            }

            aliveGlobal += spawned;
            zoneState.HasSpawned = true;
            zoneState.WipeRestUntilUtc = null;
            zoneState.NextTopUpAtUtc = now.AddSeconds(Config.SWARM_MONSTER_SUPPLY_TOP_UP_INTERVAL_SECONDS);
        }
    }

    private void ReclaimStrandedMonsters(MatchRuntime runtime, Dictionary<AreaType, List<long>> occupied, DateTime now)
    {
        var state = runtime.Monsters;
        foreach (var zone in occupied.Keys)
        {
            state.ZoneVacatedAtUtc.Remove(zone);
        }

        foreach (var monster in state.Entities.Values)
        {
            if (!monster.Alive || occupied.ContainsKey(monster.HomeArea))
            {
                continue;
            }

            if (monster.Infiltrating && monster.MarchIsPursuit)
            {
                continue;
            }

            if (!runtime.Closures.IsAreaClosed(monster.HomeArea))
            {
                if (!state.ZoneVacatedAtUtc.TryGetValue(monster.HomeArea, out var vacatedAtUtc))
                {
                    state.ZoneVacatedAtUtc[monster.HomeArea] = now;
                    continue;
                }

                if ((now - vacatedAtUtc).TotalSeconds < Config.SWARM_MONSTER_STRANDED_GRACE_SECONDS)
                {
                    continue;
                }
            }
            monster.Alive = false;
            monster.DiedAtUtc = now;
        }
    }

    public int ConsumeSupplyStoneBudget(MatchRuntime runtime, Monster monster, DateTime now)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster supply requires the match lock.");
        }
        var state = runtime.Monsters;
        int reward = monster.SummonStoneReward;
        if (reward <= 0)
        {
            return reward;
        }

        int phaseIndex = GetSupplyPhaseIndex((now - state.StartsAtUtc).TotalSeconds);
        var budgetKey = (monster.HomeArea, phaseIndex);
        if (monster.Kind == MonsterKind.RunawayGoblin)
        {
            return state.SupplyCoreRewarded.Add(budgetKey) ? reward : 0;
        }

        if (!state.SupplyStoneBucket.TryGetValue(budgetKey, out var bucket))
        {
            bucket = (Config.SWARM_MONSTER_SUPPLY_STONE_BUCKET_BURST, now);
        }

        var phases = SwarmSupplyPhaseData.GetAll();
        double until = phases[phaseIndex].UntilSeconds;
        double from = phaseIndex == 0 ? 0d : phases[phaseIndex - 1].UntilSeconds;
        double durationSeconds = until > Config.SWARM_MATCH_DURATION_SECONDS ? Math.Max(1d, Config.SWARM_MATCH_DURATION_SECONDS - from) : Math.Max(1d, until - from);
        double refillPerSecond = phases[phaseIndex].StoneBudget / durationSeconds;
        double elapsedSeconds = Math.Max(0d, (now - bucket.RefilledAtUtc).TotalSeconds);
        double available = Math.Min(Config.SWARM_MONSTER_SUPPLY_STONE_BUCKET_BURST, bucket.Available + elapsedSeconds * refillPerSecond);
        int granted = Math.Min(reward, (int)Math.Floor(available));
        state.SupplyStoneBucket[budgetKey] = (available - granted, now);

        return granted;
    }

    private static int CountAliveInArea(MatchMonsters state, AreaType area)
    {
        int count = 0;
        foreach (var monster in state.Entities.Values)
        {
            if (monster.Alive && monster.HomeArea == area)
            {
                count++;
            }
        }

        return count;
    }

    private int SpawnSupplyMonsters(MatchRuntime runtime, IReadOnlyList<PlayerPositionSnapshot> participants, AreaType area, int normals, bool includeCore, int phaseIndex, DateTime now, IReadOnlyList<long> roster)
    {
        var state = runtime.Monsters;
        var phase = SwarmSupplyPhaseData.GetAll()[phaseIndex];
        var ownerLoad = new Dictionary<long, int>();
        if (roster is { Count: > 0 })
        {
            foreach (long playerId in roster)
            {
                ownerLoad[playerId] = 0;
            }
            foreach (var candidate in state.Entities.Values)
            {
                if (!candidate.Alive || candidate.OwnerPlayerId == 0)
                {
                    continue;
                }

                if (ownerLoad.ContainsKey(candidate.OwnerPlayerId))
                {
                    ownerLoad[candidate.OwnerPlayerId]++;
                }
            }
        }

        bool infiltrate = area != AreaType.S2Corridor9;
        var inward = MapCoordinateConverter.GetAreaDirection(Config.SWARM_MATCH_MAP, area, AreaType.S2Corridor9);
        var freeAnchors = SelectSpawnAnchors(area, participants, infiltrate, inward);
        if (freeAnchors.Count == 0)
        {
            return 0;
        }
        var spawnPlan = new List<(MonsterKind Kind, Vector3f Anchor)>(normals + 1);
        for (int index = 0; index < normals; index++)
        {
            spawnPlan.Add((MonsterKind.Skeleton, freeAnchors[index % freeAnchors.Count]));
        }

        if (includeCore)
        {
            spawnPlan.Add((MonsterKind.RunawayGoblin, freeAnchors[^1]));
        }

        var insignia = (MonsterInsignia)(state.NextSupplyPackOrdinal++ % InsigniaCount);
        for (int index = 0; index < spawnPlan.Count; index++)
        {
            var packAnchor = spawnPlan[index].Anchor;
            float angle = (float)(index * Math.PI * 2d / spawnPlan.Count) + (float)(state.Rng.NextDouble() * 0.5d - 0.25d);
            var destination = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, new Vector3f(
                packAnchor.X + MathF.Cos(angle) * Config.SWARM_MONSTER_SUPPLY_SCATTER_RADIUS + inward.X * Config.SWARM_MONSTER_SUPPLY_INWARD_BIAS,
                packAnchor.Y + MathF.Sin(angle) * Config.SWARM_MONSTER_SUPPLY_SCATTER_RADIUS + inward.Y * Config.SWARM_MONSTER_SUPPLY_INWARD_BIAS,
                0f), packAnchor, area);

            var position = destination;
            var spawnArea = area;
            List<Vector3f>? route = null;
            var fieldSpawn = ResolveFieldSpawn(runtime, area, now);
            if (fieldSpawn != null)
            {
                position = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, fieldSpawn.Value.Spawn), packAnchor, area);
                var fieldAnchorWorld = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, fieldSpawn.Value.Anchor);
                destination = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, new Vector3f(fieldAnchorWorld.X + MathF.Cos(angle) * Config.SWARM_MONSTER_SUPPLY_SCATTER_RADIUS, fieldAnchorWorld.Y + MathF.Sin(angle) * Config.SWARM_MONSTER_SUPPLY_SCATTER_RADIUS, 0f), fieldAnchorWorld, area);
            }
            else if (infiltrate && movement.TryPlanInfiltration(runtime, area, destination, out var origin, out var planned))
            {
                position = origin;
                spawnArea = AreaType.S2Corridor9;
                route = planned;
            }

            var kind = spawnPlan[index].Kind;
            var definition = SwarmMonsterData.Get((int)kind) ?? SwarmMonsterData.Get((int)MonsterKind.Skeleton) ?? throw new InvalidOperationException("swarm_monster.csv must define the skeleton fallback row.");
            bool isCore = kind == MonsterKind.RunawayGoblin;
            int stoneReward = isCore ? Config.SWARM_MONSTER_SUPPLY_CORE_STONE_REWARD : definition.StoneReward;
            int maxHp = isCore ? phase.CoreHp : phase.NormalHp;
            int contactDamage = isCore ? definition.OrbDamage : phase.ContactDamage;

            int serial = state.NextSerial++;
            var monster = new Monster
            {
                MonsterId = FirstMonsterId + serial,
                CombatTargetId = FirstCombatTargetId - serial,
                Insignia = insignia,
                Area = spawnArea,
                HomeArea = area,
                Position = position,
                Health = maxHp,
                Alive = true,
                Aggro = true,
                PhaseTier = phaseIndex,
                ActivatesAtUtc = now.AddSeconds(Config.SWARM_MONSTER_SUPPLY_TELEGRAPH_SECONDS + state.Rng.NextDouble() * Config.SWARM_MONSTER_INFILTRATION_DEPARTURE_JITTER_SECONDS),
                SpawnedAtUtc = now,
                NextContactAtUtc = now,
                ScatterAngle = (float)(state.Rng.NextDouble() * Math.PI * 2d),
                SummonStoneReward = stoneReward,
                HeartReward = definition.HeartReward > 0 ? definition.HeartReward : state.Rng.NextDouble() < Config.SWARM_MONSTER_SUPPLY_HEART_DROP_CHANCE ? 1 : 0,
                ContactDamageValue = contactDamage,
                Kind = kind,
                MaxHealthValue = maxHp,
                AttackRangeValue = definition.AttackRange > 0f ? definition.AttackRange : Monster.BaseContactRadius,
                AttackCooldownValue = insignia == MonsterInsignia.Wave && !isCore ? Config.SWARM_MONSTER_WAVE_INSIGNIA_ATTACK_COOLDOWN_SECONDS : definition.AttackCooldownSeconds,
                AnchorX = fieldSpawn != null ? destination.X : position.X,
                AnchorY = fieldSpawn != null ? destination.Y : position.Y,
                OwnerPlayerId = SelectMonsterOwner(runtime, ownerLoad)
            };

            if (route != null)
            {
                monster.Infiltrating = true;
                monster.Movement.Waypoints.AddRange(route);
                monster.MarchBudgetSeconds = MonsterBehaviorService.ComputeMarchBudgetSeconds(position, route);
                monster.MarchSpeedScale = 1f + (float)(state.Rng.NextDouble() * 2d - 1d) * Config.SWARM_MONSTER_MARCH_SPEED_JITTER;
            }

            state.Entities[monster.MonsterId] = monster;
        }

        return spawnPlan.Count;
    }

    private long SelectMonsterOwner(MatchRuntime runtime, Dictionary<long, int> ownerLoad)
    {
        if (ownerLoad.Count == 0)
        {
            return 0;
        }

        long chosen = 0;
        int least = int.MaxValue;
        bool chosenOrbless = false;
        foreach ((long playerId, int load) in ownerLoad)
        {
            bool orbless = !runtime.GetOrbs(playerId).HasAnyOrb();
            if (chosenOrbless && !orbless)
            {
                continue;
            }
            if (orbless && !chosenOrbless)
            {
                chosenOrbless = true;
                least = load;
                chosen = playerId;
                continue;
            }
            if (load >= least)
            {
                continue;
            }
            least = load;
            chosen = playerId;
        }

        if (chosen != 0)
        {
            ownerLoad[chosen] = least + 1;
        }
        return chosen;
    }

    private List<Vector3f> SelectSpawnAnchors(AreaType area, IReadOnlyList<PlayerPositionSnapshot> participants, bool infiltrate, Vector3f inward)
    {
        var center = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area));
        var anchors = new List<Vector3f>(CampsPerArea);
        for (int anchorIndex = 0; anchorIndex < CampsPerArea; anchorIndex++)
        {
            var customAnchorCell = GameMonsterCampData.GetAnchor(area, anchorIndex);
            if (customAnchorCell != null)
            {
                anchors.Add(MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, MapPathfinder.InsetCellFromAreaEdge(Config.SWARM_MATCH_MAP, customAnchorCell, area, AnchorAreaEdgeMargin)), center, area));
                continue;
            }

            float anchorAngle = anchorIndex * (MathF.Tau / CampsPerArea);
            anchors.Add(MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, new Vector3f(center.X + Config.SWARM_MONSTER_CAMP_ANCHOR_RADIUS * 0.6f * MathF.Cos(anchorAngle), center.Y + Config.SWARM_MONSTER_CAMP_ANCHOR_RADIUS * 0.6f * MathF.Sin(anchorAngle), 0f), center, area));
        }

        var ranked = new List<(Vector3f Anchor, float Distance, float Inwardness, int Order)>(anchors.Count);
        foreach (var anchor in anchors)
        {
            float nearestSquared = float.MaxValue;
            foreach (var participant in participants)
            {
                if (participant.Area != area)
                {
                    continue;
                }
                float dx = participant.Position.X - anchor.X;
                float dy = participant.Position.Y - anchor.Y;
                nearestSquared = Math.Min(nearestSquared, dx * dx + dy * dy);
            }
            float distance = nearestSquared == float.MaxValue ? float.MaxValue : MathF.Sqrt(nearestSquared);
            if (!infiltrate && distance < Config.SWARM_MONSTER_SUPPLY_SAFE_SPAWN_DISTANCE)
            {
                continue;
            }
            float inwardness = (anchor.X - center.X) * inward.X + (anchor.Y - center.Y) * inward.Y;
            ranked.Add((anchor, distance, inwardness, ranked.Count));
        }
        if (ranked.Count == 0)
        {
            return [];
        }
        ranked.Sort(static (left, right) =>
        {
            bool leftOffscreen = left.Distance >= Config.SWARM_MONSTER_SUPPLY_OFFSCREEN_DISTANCE;
            bool rightOffscreen = right.Distance >= Config.SWARM_MONSTER_SUPPLY_OFFSCREEN_DISTANCE;
            if (leftOffscreen != rightOffscreen)
            {
                return leftOffscreen ? -1 : 1;
            }
            if (left.Inwardness != right.Inwardness)
            {
                return right.Inwardness.CompareTo(left.Inwardness);
            }
            if (left.Distance != right.Distance)
            {
                return right.Distance.CompareTo(left.Distance);
            }
            return left.Order.CompareTo(right.Order);
        });

        var freeAnchors = new List<Vector3f>(ranked.Count);
        foreach (var entry in ranked)
        {
            freeAnchors.Add(entry.Anchor);
        }
        return freeAnchors;
    }

    private (Cell Spawn, Cell Anchor)? ResolveFieldSpawn(MatchRuntime runtime, AreaType area, DateTime now)
    {
        var state = runtime.Monsters;
        (Cell Spawn, Cell Anchor)? fieldSpawn = null;
        var cells = SwarmPressureField.GetAreaCellsByDistance(area);
        if (cells.Count > 0)
        {
            double safeDistance = runtime.Closures.GetSafeDistance(now);
            bool boundaryCrossing = safeDistance < cells[^1].Distance;
            double spawnMin = boundaryCrossing ? safeDistance : cells[^1].Distance - Config.SWARM_MONSTER_FIELD_SPAWN_BAND_CELLS;
            double spawnMax = boundaryCrossing ? safeDistance + Config.SWARM_MONSTER_FIELD_SPAWN_BAND_CELLS : cells[^1].Distance;
            var spawnBand = new List<Cell>();
            var anchorBand = new List<Cell>();
            double anchorMax = cells[0].Distance + Config.SWARM_MONSTER_FIELD_SPAWN_BAND_CELLS;
            foreach (var entry in cells)
            {
                if (entry.Distance > spawnMin && entry.Distance <= spawnMax)
                {
                    spawnBand.Add(entry.Cell);
                }
                if (entry.Distance < anchorMax)
                {
                    anchorBand.Add(entry.Cell);
                }
            }
            if (spawnBand.Count == 0)
            {
                if (boundaryCrossing)
                {
                    foreach (var entry in cells)
                    {
                        if (entry.Distance > safeDistance)
                        {
                            spawnBand.Add(entry.Cell);
                        }
                    }
                }
                if (spawnBand.Count == 0)
                {
                    spawnBand.Add(cells[^1].Cell);
                }
            }
            if (anchorBand.Count == 0)
            {
                anchorBand.Add(cells[0].Cell);
            }
            fieldSpawn = (spawnBand[state.Rng.Next(spawnBand.Count)], anchorBand[state.Rng.Next(anchorBand.Count)]);
        }
        return fieldSpawn;
    }
}
