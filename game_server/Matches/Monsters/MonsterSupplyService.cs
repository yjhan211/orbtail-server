using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

internal sealed class MonsterSupplyService
{
    private const double SupplyTopUpIntervalSeconds = 12d;
    private const int SupplyTopUpCount = 30;
    private const double SupplyHeartDropChance = 0.03d;
    private const double SupplyWipeRestSeconds = 2d;
    private const double SupplyWipeRestSecondsFinalPhase = 0d;
    private const float SupplySafeSpawnDistance = 2.5f;
    private const float SupplyOffscreenDistance = 7f;
    private const float SupplyInwardBias = 2.2f;
    private const double SupplyBlockedRetrySeconds = 1d;
    private const int SupplyCoreStoneReward = 3;
    private const int SupplyCoreFirstPhaseIndex = 2;
    private const int SupplyGlobalAliveHardCap = 420;
    private const int SupplyZoneCrowdCap = 4;
    private const double SupplyCrowdExtraRatio = 0.5d;
    private const int SupplyCrowdLinearPlayers = 2;
    private const float SupplyTelegraphSeconds = 0.4f;
    private const float SupplyScatterRadius = 1.6f;
    private const int FieldSpawnBandCells = 6;
    private const int CampsPerArea = 3;
    private const float CampAnchorRadius = 6f;
    private const float InfiltrationOriginRadius = 3.5f;
    private const double InfiltrationGoldenAngle = 0.6180339887498949d;
    private const float InfiltrationOriginJitterRadians = 1.3963f;
    private const float InfiltrationBurstDistance = 2.0f;
    private const float MarchLaneOffsetMax = 0.4f;
    private const float MarchSpeedJitter = 0.1f;
    private const double InfiltrationDepartureJitterSeconds = 0.4d;
    private const float WaveInsigniaAttackCooldownSeconds = 2.2f;
    private const int FirstMonsterId = 7_000_000;
    private const long FirstCombatTargetId = -4_000_000_000_000_000_000L;
    private const double StrandedMonsterGraceSeconds = 6d;
    private const double SupplyStoneBucketBurst = 5d;
    private const int AnchorAreaEdgeMargin = 3;

    private static readonly AreaType SwarmInwardOriginArea = Config.SWARM_MATCH_GROUND_AREA;
    private static readonly SwarmSupplyPhaseDefinition[] DefaultSupplyPhases =
    [
        new(0, 100d, 8, 16, 10, 48, 90), // 0:00~1:40 폐쇄 전
        new(1, 150d, 12, 17, 14, 60, 100), // 1:40~2:30 1차
        new(2, 200d, 16, 19, 20, 72, 110), // 2:30~3:20 2차
        new(3, 250d, 22, 21, 28, 96, 120), // 3:20~4:10 3차
        new(4, double.MaxValue, 28, 22, 40, 120, 120) // 4:10~5:00 최종 수렴
    ];
    private static IReadOnlyList<SwarmSupplyPhaseDefinition> SupplyPhases => SwarmSupplyPhaseData.IsLoaded ? SwarmSupplyPhaseData.GetAll() : DefaultSupplyPhases;

    private static int GetSupplyPhaseIndex(double elapsedSeconds)
    {
        var phases = SupplyPhases;
        for (int index = 0; index < phases.Count; index++)
        {
            if (elapsedSeconds < phases[index].UntilSeconds)
                return index;
        }
        return phases.Count - 1;
    }

    private static int GetSupplyZoneTarget(int perPlayerTarget, int playersInZone)
    {
        int players = Math.Clamp(playersInZone, 1, SupplyZoneCrowdCap);
        int linearPlayers = Math.Min(players, SupplyCrowdLinearPlayers);
        int crowdPlayers = players - linearPlayers;
        return perPlayerTarget * linearPlayers + (int)Math.Round(perPlayerTarget * SupplyCrowdExtraRatio * crowdPlayers);
    }

    public void ProcessTick(MatchRuntime runtime, DateTime now, MonsterTickResult result, bool preMatch)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster supply requires the match lock.");
        }
        var state = runtime.Monsters;
        double elapsed = (now - state.StartsAtUtc).TotalSeconds;
        state.MaxParticipantCount = Math.Max(state.MaxParticipantCount, state.LastParticipants.Length);
        int phaseIndex = GetSupplyPhaseIndex(elapsed);
        var phase = SupplyPhases[phaseIndex];
        var occupied = new Dictionary<AreaType, List<long>>();
        foreach (var participant in state.LastParticipants)
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

        foreach (var zone in state.SupplyZones.Keys.Where(zone => !occupied.ContainsKey(zone)).ToList())
        {
            state.SupplyZones.Remove(zone);
        }

        ReclaimStrandedMonsters(runtime, occupied, now);
        int aliveGlobal = CountAliveGlobal(state);
        int globalCap = Math.Min(SupplyGlobalAliveHardCap, occupied.Values.Sum(roster => GetSupplyZoneTarget(phase.PerPlayerTarget, roster.Count)));

        foreach (var (zone, roster) in occupied.OrderBy(pair => CountAliveInArea(state, pair.Key)))
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
                    bool finalPhase = phaseIndex == SupplyPhases.Count - 1;
                    zoneState.WipeRestUntilUtc = now.AddSeconds(finalPhase ? SupplyWipeRestSecondsFinalPhase : SupplyWipeRestSeconds);
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

            bool includeCore = phaseIndex >= SupplyCoreFirstPhaseIndex && !HasAliveCore(state, zone) && aliveInZone < zoneTarget && aliveGlobal < globalCap;
            int room = zoneTarget - aliveInZone - (includeCore ? 1 : 0);
            int want = Math.Min(SupplyTopUpCount, room);
            want = Math.Min(want, globalCap - aliveGlobal - (includeCore ? 1 : 0));
            if (want <= 0 && !includeCore)
            {
                continue;
            }

            want = Math.Max(0, want);
            int spawned = SpawnSupplyMonsters(runtime, zone, want, includeCore, phaseIndex, now, result, roster);
            if (spawned == 0)
            {
                zoneState.NextTopUpAtUtc = now.AddSeconds(SupplyBlockedRetrySeconds);
                continue;
            }

            aliveGlobal += spawned;
            zoneState.HasSpawned = true;
            zoneState.WipeRestUntilUtc = null;
            zoneState.NextTopUpAtUtc = now.AddSeconds(SupplyTopUpIntervalSeconds);
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

                if ((now - vacatedAtUtc).TotalSeconds < StrandedMonsterGraceSeconds)
                {
                    continue;
                }
            }
            monster.Alive = false;
            monster.DiedAtUtc = now;
        }
    }

    private static int CountAliveGlobal(MatchMonsterState state) => state.Entities.Values.Count(monster => monster.Alive);

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
            bucket = (SupplyStoneBucketBurst, now);
        }

        double refillPerSecond = GetSupplyStoneRefillPerSecond(phaseIndex);
        double elapsedSeconds = Math.Max(0d, (now - bucket.RefilledAtUtc).TotalSeconds);
        double available = Math.Min(SupplyStoneBucketBurst, bucket.Available + elapsedSeconds * refillPerSecond);
        int granted = Math.Min(reward, (int)Math.Floor(available));
        state.SupplyStoneBucket[budgetKey] = (available - granted, now);
        return granted;
    }

    private static double GetSupplyStoneRefillPerSecond(int phaseIndex)
    {
        double until = SupplyPhases[phaseIndex].UntilSeconds;
        double from = phaseIndex == 0 ? 0d : SupplyPhases[phaseIndex - 1].UntilSeconds;
        double durationSeconds = until > Config.SWARM_MATCH_DURATION_SECONDS ? Math.Max(1d, Config.SWARM_MATCH_DURATION_SECONDS - from) : Math.Max(1d, until - from);
        return SupplyPhases[phaseIndex].StoneBudget / durationSeconds;
    }

    private static bool HasAliveCore(MatchMonsterState state, AreaType area) =>
        state.Entities.Values.Any(monster => monster.Alive && monster.HomeArea == area && monster.Kind == MonsterKind.RunawayGoblin);

    private static int CountAliveInArea(MatchMonsterState state, AreaType area) =>
        state.Entities.Values.Count(monster => monster.Alive && monster.HomeArea == area);

    private int SpawnSupplyMonsters(MatchRuntime runtime, AreaType area, int normals, bool includeCore, int phaseIndex, DateTime now, MonsterTickResult result, IReadOnlyList<long> roster)
    {
        var state = runtime.Monsters;
        var phase = SupplyPhases[phaseIndex];
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

        long ClaimOwner()
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

        bool infiltrate = area != SwarmInwardOriginArea;
        var center = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area));
        var anchors = new List<Vector3f>(CampsPerArea);
        for (int anchorIndex = 0; anchorIndex < CampsPerArea; anchorIndex++)
        {
            var customAnchorCell = GameMonsterCampData.GetAnchor(area, anchorIndex);
            if (customAnchorCell != null)
            {
                anchors.Add(MonsterNavigation.ClampToAreaWalkable(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, InsetAnchorFromAreaEdge(customAnchorCell, area)), center, area));
                continue;
            }

            float anchorAngle = anchorIndex * 2.0944f;
            anchors.Add(MonsterNavigation.ClampToAreaWalkable(new Vector3f(center.X + CampAnchorRadius * 0.6f * MathF.Cos(anchorAngle), center.Y + CampAnchorRadius * 0.6f * MathF.Sin(anchorAngle), 0f), center, area));
        }

        var inward = GetInwardDirection(area);
        var ranked = anchors
            .Select(anchor => (
                Anchor: anchor,
                Distance: NearestParticipantDistance(state, area, anchor),
                Inwardness: (anchor.X - center.X) * inward.X + (anchor.Y - center.Y) * inward.Y))
            .Where(entry => infiltrate || entry.Distance >= SupplySafeSpawnDistance)
            .OrderByDescending(entry => entry.Distance >= SupplyOffscreenDistance)
            .ThenByDescending(entry => entry.Inwardness)
            .ThenByDescending(entry => entry.Distance)
            .ToList();
        if (ranked.Count == 0)
        {
            return 0;
        }

        var freeAnchors = ranked.Select(entry => entry.Anchor).ToList();
        var spawnPlan = new List<(MonsterKind Kind, Vector3f Anchor)>(normals + 1);
        for (int index = 0; index < normals; index++)
        {
            spawnPlan.Add((MonsterKind.Skeleton, freeAnchors[index % freeAnchors.Count]));
        }

        if (includeCore)
        {
            spawnPlan.Add((MonsterKind.RunawayGoblin, freeAnchors[^1]));
        }

        int stoneTotal = 0;
        var insignia = (MonsterInsignia)(state.NextSupplyPackOrdinal++ % 3);
        for (int index = 0; index < spawnPlan.Count; index++)
        {
            var packAnchor = spawnPlan[index].Anchor;
            float angle = (float)(index * Math.PI * 2d / spawnPlan.Count) + (float)(state.Rng.NextDouble() * 0.5d - 0.25d);
            var destination = MonsterNavigation.ClampToAreaWalkable(new Vector3f(
                packAnchor.X + MathF.Cos(angle) * SupplyScatterRadius + inward.X * SupplyInwardBias,
                packAnchor.Y + MathF.Sin(angle) * SupplyScatterRadius + inward.Y * SupplyInwardBias,
                0f), packAnchor, area);

            var position = destination;
            var spawnArea = area;
            List<Vector3f>? route = null;
            var fieldSpawn = ResolveFieldSpawn(runtime.Closures.GetSafeDistance(now), area);
            if (fieldSpawn != null)
            {
                position = MonsterNavigation.ClampToAreaWalkable(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, fieldSpawn.Value.Spawn), packAnchor, area);
                var fieldAnchorWorld = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, fieldSpawn.Value.Anchor);
                destination = MonsterNavigation.ClampToAreaWalkable(new Vector3f(fieldAnchorWorld.X + MathF.Cos(angle) * SupplyScatterRadius, fieldAnchorWorld.Y + MathF.Sin(angle) * SupplyScatterRadius, 0f), fieldAnchorWorld, area);
            }
            else if (infiltrate && TryPlanInfiltration(runtime, area, destination, out var origin, out var planned))
            {
                position = origin;
                spawnArea = SwarmInwardOriginArea;
                route = planned;
            }

            var kind = spawnPlan[index].Kind;
            var definition = SwarmMonsterData.Get((int)kind) ?? SwarmMonsterData.Get((int)MonsterKind.Skeleton)
                ?? throw new InvalidOperationException("swarm_monster.csv must define the skeleton fallback row.");
            bool isCore = kind == MonsterKind.RunawayGoblin;
            int stoneReward = isCore ? SupplyCoreStoneReward : definition.StoneReward;
            int maxHp = isCore ? phase.CoreHp : phase.NormalHp;
            int contactDamage = isCore ? definition.OrbDamage : phase.ContactDamage;

            stoneTotal += stoneReward;
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
                ActivatesAtUtc = now.AddSeconds(SupplyTelegraphSeconds + state.Rng.NextDouble() * InfiltrationDepartureJitterSeconds),
                SpawnedAtUtc = now,
                NextContactAtUtc = now,
                ScatterAngle = (float)(state.Rng.NextDouble() * Math.PI * 2d),
                SummonStoneReward = stoneReward,
                HeartReward = definition.HeartReward > 0 ? definition.HeartReward : state.Rng.NextDouble() < SupplyHeartDropChance ? 1 : 0,
                BootsReward = definition.BootsReward,
                KeyReward = definition.KeyReward,
                ContactDamageValue = contactDamage,
                Kind = kind,
                MaxHealthValue = maxHp,
                AttackRangeValue = definition.AttackRange > 0f ? definition.AttackRange : Monster.BaseContactRadius,
                AttackCooldownValue = insignia == MonsterInsignia.Wave && !isCore ? WaveInsigniaAttackCooldownSeconds : definition.AttackCooldownSeconds,
                AnchorX = fieldSpawn != null ? destination.X : position.X,
                AnchorY = fieldSpawn != null ? destination.Y : position.Y,
                OwnerPlayerId = ClaimOwner()
            };

            if (route != null)
            {
                monster.Infiltrating = true;
                monster.MarchWaypoints.AddRange(route);
                monster.MarchBudgetSeconds = MonsterNavigation.ComputeMarchBudgetSeconds(position, route);
                monster.MarchLaneOffset = (float)(state.Rng.NextDouble() * 2d - 1d) * MarchLaneOffsetMax;
                monster.MarchSpeedScale = 1f + (float)(state.Rng.NextDouble() * 2d - 1d) * MarchSpeedJitter;
            }

            state.Entities[monster.MonsterId] = monster;
            result.SpawnedMonsters.Add(monster.ToMonsterRuntimeInfo());
        }

        result.SupplyPackSpawns.Add(new SupplyPackSpawnInfo(area, phaseIndex, spawnPlan.Count, stoneTotal));
        return spawnPlan.Count;
    }

    private static bool TryPlanInfiltration(MatchRuntime runtime, AreaType destinationArea, Vector3f destination, out Vector3f origin, out List<Vector3f> route)
    {
        route = null!;
        var originCenter = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, SwarmInwardOriginArea));
        var state = runtime.Monsters;
        float baseAngle = ResolveInfiltrationExitBearing(runtime, destinationArea, destination, originCenter);
        double golden = (state.NextInfiltrationOriginOrdinal++ * InfiltrationGoldenAngle) % 1d;
        float angle = baseAngle + (float)((golden - 0.5d) * 2d) * InfiltrationOriginJitterRadians;
        origin = MonsterNavigation.ClampToAreaWalkable(new Vector3f(originCenter.X + MathF.Cos(angle) * InfiltrationOriginRadius, originCenter.Y + MathF.Sin(angle) * InfiltrationOriginRadius, 0f), originCenter, SwarmInwardOriginArea);

        var burst = MonsterNavigation.ClampToAreaWalkable(new Vector3f(origin.X + MathF.Cos(angle) * InfiltrationBurstDistance, origin.Y + MathF.Sin(angle) * InfiltrationBurstDistance, 0f), originCenter, SwarmInwardOriginArea);
        if (MonsterNavigation.IsSegmentWalkable(origin, burst) && MonsterNavigation.TryPlanRoute(SwarmInwardOriginArea, burst, destinationArea, destination, candidate => IsInfiltrationRouteBlocked(runtime, candidate, destinationArea), out route))
        {
            route.Insert(0, burst);
            return true;
        }

        return MonsterNavigation.TryPlanRoute(SwarmInwardOriginArea, origin, destinationArea, destination, candidate => IsInfiltrationRouteBlocked(runtime, candidate, destinationArea), out route);
    }

    private static float ResolveInfiltrationExitBearing(MatchRuntime runtime, AreaType destinationArea, Vector3f destination, Vector3f originCenter)
    {
        var bearings = runtime.Monsters.InfiltrationExitBearings;
        if (bearings.TryGetValue(destinationArea, out float cached))
        {
            return cached;
        }

        float fallback = MathF.Atan2(destination.Y - originCenter.Y, destination.X - originCenter.X);
        if (!MonsterNavigation.TryPlanRoute(SwarmInwardOriginArea, originCenter, destinationArea, destination, candidate => IsInfiltrationRouteBlocked(runtime, candidate, destinationArea), out var probe))
        {
            return fallback;
        }

        float exitRadius = InfiltrationOriginRadius + InfiltrationBurstDistance;
        foreach (var point in probe)
        {
            float dx = point.X - originCenter.X;
            float dy = point.Y - originCenter.Y;
            if (dx * dx + dy * dy < exitRadius * exitRadius)
            {
                continue;
            }
            float bearing = MathF.Atan2(dy, dx);
            bearings[destinationArea] = bearing;
            return bearing;
        }

        bearings[destinationArea] = fallback;
        return fallback;
    }

    private static bool IsInfiltrationRouteBlocked(MatchRuntime runtime, AreaType candidate, AreaType destinationArea)
    {
        if (runtime.Closures.IsAreaClosed(candidate))
        {
            return true;
        }

        if (candidate == destinationArea)
        {
            return false;
        }

        var rooms = MatchSpawnData.GetPhaseRoomCandidates();
        foreach (var t in rooms)
        {
            if (t == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private static Vector3f GetInwardDirection(AreaType area)
    {
        if (area == SwarmInwardOriginArea)
        {
            return new Vector3f(0f, 0f, 0f);
        }

        var center = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area));
        var origin = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, SwarmInwardOriginArea));
        float dx = origin.X - center.X;
        float dy = origin.Y - center.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        return length < 0.001f ? new Vector3f(0f, 0f, 0f) : new Vector3f(dx / length, dy / length, 0f);
    }

    private static float NearestParticipantDistance(MatchMonsterState state, AreaType area, Vector3f anchor)
    {
        float nearestSquared = float.MaxValue;
        foreach (var participant in state.LastParticipants)
        {
            if (participant.Area != area)
            {
                continue;
            }
            float dx = participant.Position.X - anchor.X;
            float dy = participant.Position.Y - anchor.Y;
            nearestSquared = Math.Min(nearestSquared, dx * dx + dy * dy);
        }

        return nearestSquared == float.MaxValue ? float.MaxValue : MathF.Sqrt(nearestSquared);
    }

    public static Cell InsetAnchorFromAreaEdge(Cell cell, AreaType area)
    {
        var region = GameMapData.GetAreas(Config.SWARM_MATCH_MAP).FirstOrDefault(candidate => candidate.AreaType == area);
        if (region == null)
        {
            return cell;
        }

        int minX = region.Start.X + AnchorAreaEdgeMargin;
        int maxX = region.End.X - AnchorAreaEdgeMargin;
        int minY = region.Start.Y + AnchorAreaEdgeMargin;
        int maxY = region.End.Y - AnchorAreaEdgeMargin;
        if (minX > maxX || minY > maxY)
        {
            return cell;
        }

        int insetX = Math.Clamp(cell.X, minX, maxX);
        int insetY = Math.Clamp(cell.Y, minY, maxY);
        return insetX == cell.X && insetY == cell.Y ? cell : new Cell(insetX, insetY);
    }

    private static (Cell Spawn, Cell Anchor)? ResolveFieldSpawn(double safeDistance, AreaType area)
    {
        var cells = SwarmPressureField.GetAreaCellsByDistance(area);
        if (cells.Count == 0)
        {
            return null;
        }

        bool boundaryCrossing = safeDistance < cells[^1].Distance;
        double spawnMin = boundaryCrossing ? safeDistance : cells[^1].Distance - FieldSpawnBandCells;
        double spawnMax = boundaryCrossing ? safeDistance + FieldSpawnBandCells : cells[^1].Distance;

        var spawnBand = cells.Where(entry => entry.Distance > spawnMin && entry.Distance <= spawnMax).ToList();
        if (spawnBand.Count == 0)
        {
            spawnBand = boundaryCrossing ? cells.Where(entry => entry.Distance > safeDistance).ToList() : [cells[^1]];
        }

        var anchorBand = cells.Where(entry => entry.Distance < cells[0].Distance + FieldSpawnBandCells).ToList();
        if (anchorBand.Count == 0)
        {
            anchorBand = [cells[0]];
        }

        return (spawnBand[Random.Shared.Next(spawnBand.Count)].Cell, anchorBand[Random.Shared.Next(anchorBand.Count)].Cell);
    }
}
