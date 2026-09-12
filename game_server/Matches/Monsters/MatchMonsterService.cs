using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>피해 적용 결과. 처치했으면 Monster가 죽은 개체이고 SummonStoneReward는 예산에서 떼어낸 드롭 수다.</summary>
internal readonly record struct MonsterDamageResult(bool Applied, bool Killed, Monster? Monster, int SummonStoneReward)
{
    public static MonsterDamageResult None => new(false, false, null, 0);
}

internal sealed class MatchMonsterService(MonsterMovementService movement)
{
    // 식별자와 구조 상수
    private const long CombatTargetIdUpperBound = -1_000_000_000_000L;
    private const double DeadPruneAfterSeconds = 3d;
    private const int CampsPerArea = 3;
    private const double InfiltrationGoldenAngle = 0.6180339887498949d;
    private const int FirstMonsterId = 7_000_000;
    private const long FirstCombatTargetId = -4_000_000_000_000_000_000L;
    private const int AnchorAreaEdgeMargin = 3;

    // 공급 기준 데이터
    private static readonly AreaType SwarmInwardOriginArea = Config.SWARM_MATCH_GROUND_AREA;
    private static IReadOnlyList<SwarmSupplyPhaseDefinition> SupplyPhases => SwarmSupplyPhaseData.GetAll();

    // 접촉 공격과 시간대별 강화
    private static double EscalationStage1AtSeconds => Config.SWARM_MONSTER_ESCALATION_STAGE1_AT_SECONDS;
    private static double EscalationStage2AtSeconds => Config.SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS;
    private static float EscalationStage2MoveSpeedMultiplier => Config.SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER;
    public static float ContactImmunitySeconds => Config.SWARM_MONSTER_CONTACT_IMMUNITY_SECONDS;
    public static float BowlerSplashRadius => Config.SWARM_MONSTER_BOWLER_SPLASH_RADIUS;
    public static float WaveInsigniaSplashRadius => Config.SWARM_MONSTER_WAVE_SPLASH_RADIUS;
    private static float WaveInsigniaAttackCooldownSeconds => Config.SWARM_MONSTER_WAVE_INSIGNIA_ATTACK_COOLDOWN_SECONDS;

    // 공급·보상·배치 설정
    private static double SupplyTopUpIntervalSeconds => Config.SWARM_MONSTER_SUPPLY_TOP_UP_INTERVAL_SECONDS;
    private static int SupplyTopUpCount => Config.SWARM_MONSTER_SUPPLY_TOP_UP_COUNT;
    private static double SupplyHeartDropChance => Config.SWARM_MONSTER_SUPPLY_HEART_DROP_CHANCE;
    private static double SupplyWipeRestSeconds => Config.SWARM_MONSTER_SUPPLY_WIPE_REST_SECONDS;
    private static double SupplyWipeRestSecondsFinalPhase => Config.SWARM_MONSTER_SUPPLY_WIPE_REST_SECONDS_FINAL_PHASE;
    private static float SupplySafeSpawnDistance => Config.SWARM_MONSTER_SUPPLY_SAFE_SPAWN_DISTANCE;
    private static float SupplyOffscreenDistance => Config.SWARM_MONSTER_SUPPLY_OFFSCREEN_DISTANCE;
    private static float SupplyInwardBias => Config.SWARM_MONSTER_SUPPLY_INWARD_BIAS;
    private static double SupplyBlockedRetrySeconds => Config.SWARM_MONSTER_SUPPLY_BLOCKED_RETRY_SECONDS;
    private static int SupplyCoreStoneReward => Config.SWARM_MONSTER_SUPPLY_CORE_STONE_REWARD;
    private static int SupplyCoreFirstPhaseIndex => Config.SWARM_MONSTER_SUPPLY_CORE_FIRST_PHASE_INDEX;
    private static int SupplyGlobalAliveHardCap => Config.SWARM_MONSTER_SUPPLY_GLOBAL_ALIVE_HARD_CAP;
    private static int SupplyZoneCrowdCap => Config.SWARM_MONSTER_SUPPLY_ZONE_CROWD_CAP;
    private static double SupplyCrowdExtraRatio => Config.SWARM_MONSTER_SUPPLY_CROWD_EXTRA_RATIO;
    private static int SupplyCrowdLinearPlayers => Config.SWARM_MONSTER_SUPPLY_CROWD_LINEAR_PLAYERS;
    private static float SupplyTelegraphSeconds => Config.SWARM_MONSTER_SUPPLY_TELEGRAPH_SECONDS;
    private static float SupplyScatterRadius => Config.SWARM_MONSTER_SUPPLY_SCATTER_RADIUS;
    private static int FieldSpawnBandCells => Config.SWARM_MONSTER_FIELD_SPAWN_BAND_CELLS;
    private static float CampAnchorRadius => Config.SWARM_MONSTER_CAMP_ANCHOR_RADIUS;
    private static float InfiltrationOriginRadius => Config.SWARM_MONSTER_INFILTRATION_ORIGIN_RADIUS;
    private static float InfiltrationOriginJitterRadians => Config.SWARM_MONSTER_INFILTRATION_ORIGIN_JITTER_RADIANS;
    private static float InfiltrationBurstDistance => Config.SWARM_MONSTER_INFILTRATION_BURST_DISTANCE;
    private static float MarchLaneOffsetMax => Config.SWARM_MONSTER_MARCH_LANE_OFFSET_MAX;
    private static float MarchSpeedJitter => Config.SWARM_MONSTER_MARCH_SPEED_JITTER;
    private static double InfiltrationDepartureJitterSeconds => Config.SWARM_MONSTER_INFILTRATION_DEPARTURE_JITTER_SECONDS;
    private static double StrandedMonsterGraceSeconds => Config.SWARM_MONSTER_STRANDED_GRACE_SECONDS;
    private static double SupplyStoneBucketBurst => Config.SWARM_MONSTER_SUPPLY_STONE_BUCKET_BURST;

    public static bool IsCombatTargetId(long actorId) => actorId < CombatTargetIdUpperBound;
    private static int GetEscalationStage(double elapsedSeconds) => elapsedSeconds >= EscalationStage2AtSeconds ? 2 : elapsedSeconds >= EscalationStage1AtSeconds ? 1 : 0;

    public List<MonsterContactDamage> ProcessTick(MatchRuntime runtime, IReadOnlyCollection<PlayerPositionSnapshot> participants, bool isGameplayActive, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var contacts = new List<MonsterContactDamage>();
        var state = runtime.Monsters;
        if (!state.IsInitialized)
        {
            return contacts;
        }

        var now = nowUtc;
        double deltaSeconds = Math.Clamp((now - state.LastTickAtUtc).TotalSeconds, 0d, 0.25d);
        state.LastTickAtUtc = now;
        state.LastParticipants = participants.ToArray();

        bool preMatch = !isGameplayActive;
        double moveDeltaSeconds = GetEscalationStage((now - state.StartsAtUtc).TotalSeconds) >= 2
            ? deltaSeconds * EscalationStage2MoveSpeedMultiplier
            : deltaSeconds;

        ProcessSupply(runtime, now, preMatch);
        foreach (var monster in state.Entities.Values)
        {
            if (!monster.Alive || now < monster.ActivatesAtUtc)
            {
                continue;
            }

            movement.Move(runtime, monster, now, moveDeltaSeconds, preMatch);
            movement.RescueMonsterFromBlockedCell(monster);
            if (now < monster.NextContactAtUtc)
            {
                continue;
            }

            float attackRange = monster.Aggro && monster.AttackRangeValue > Monster.BaseContactRadius ? monster.AttackRangeValue : Monster.GetContactRadius(monster.Kind);
            const float verticalScale = 2f;
            foreach (var participant in state.LastParticipants)
            {
                if (participant.Area != monster.Area)
                {
                    continue;
                }
                float dx = monster.Position.X - participant.Position.X;
                float dy = (monster.Position.Y - participant.Position.Y) * verticalScale;
                if (dx * dx + dy * dy > attackRange * attackRange)
                {
                    continue;
                }

                if (state.ContactImmuneUntilUtc.TryGetValue(participant.PlayerId, out var immuneUntil) && now < immuneUntil)
                {
                    continue;
                }

                monster.NextContactAtUtc = now.AddSeconds(monster.AttackCooldownValue);
                state.ContactImmuneUntilUtc[participant.PlayerId] = now.AddSeconds(ContactImmunitySeconds);
                monster.Aggro = true;
                monster.ChaseTargetPlayerId = participant.PlayerId;
                contacts.Add(new MonsterContactDamage(monster.MonsterId, participant.PlayerId, monster.Area, monster.ContactDamageValue));
                bool waveInsignia = monster.Insignia == MonsterInsignia.Wave;
                if (waveInsignia || monster.Kind == MonsterKind.Bowler)
                {
                    float splashRadius = waveInsignia ? WaveInsigniaSplashRadius : BowlerSplashRadius;
                    foreach (var splashed in state.LastParticipants)
                    {
                        if (splashed.PlayerId == participant.PlayerId || splashed.Area != monster.Area)
                        {
                            continue;
                        }
                        float sx = splashed.Position.X - participant.Position.X;
                        float sy = splashed.Position.Y - participant.Position.Y;
                        if (sx * sx + sy * sy > splashRadius * splashRadius)
                        {
                            continue;
                        }

                        if (state.ContactImmuneUntilUtc.TryGetValue(splashed.PlayerId, out var splashImmune) && now < splashImmune)
                        {
                            continue;
                        }
                        state.ContactImmuneUntilUtc[splashed.PlayerId] = now.AddSeconds(ContactImmunitySeconds);
                        contacts.Add(new MonsterContactDamage(monster.MonsterId, splashed.PlayerId, monster.Area, monster.ContactDamageValue));
                    }
                }
                break;
            }
        }
        var expired = new List<int>();
        foreach (var monster in state.Entities.Values)
        {
            if (!monster.Alive && (now - monster.DiedAtUtc).TotalSeconds > DeadPruneAfterSeconds)
            {
                expired.Add(monster.MonsterId);
            }
        }
        foreach (int monsterId in expired)
        {
            state.Entities.Remove(monsterId);
        }
        return contacts;
    }

    public MonsterDamageResult ApplyMonsterDamage(MatchRuntime runtime, long combatTargetId, long attackerPlayerId, int damage, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var state = runtime.Monsters;
        if (damage <= 0 || !state.IsInitialized)
        {
            return MonsterDamageResult.None;
        }

        var monster = state.FindByCombatTarget(combatTargetId);
        if (monster != null)
        {
            monster.PendingDamage = Math.Max(0, monster.PendingDamage - damage);
        }

        if (monster is not { Alive: true })
        {
            return MonsterDamageResult.None;
        }

        monster.Aggro = true;
        if (monster.OwnerPlayerId == 0)
        {
            monster.ChaseTargetPlayerId = attackerPlayerId;
        }
        foreach (var mate in state.Entities.Values)
        {
            if (!mate.Alive || mate.Aggro || mate.Area != monster.Area)
            {
                continue;
            }
            mate.Aggro = true;
            if (mate.OwnerPlayerId == 0)
            {
                mate.ChaseTargetPlayerId = attackerPlayerId;
            }
        }
        monster.Health = Math.Max(0, monster.Health - damage);
        bool killed = monster.Health == 0;
        int summonStoneReward = 0;
        if (killed)
        {
            monster.Alive = false;
            monster.DiedAtUtc = nowUtc;
            summonStoneReward = ConsumeSupplyStoneBudget(runtime, monster, nowUtc);
        }

        return new MonsterDamageResult(true, killed, monster, summonStoneReward);
    }


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

    private void ProcessSupply(MatchRuntime runtime, DateTime now, bool preMatch)
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
            int spawned = SpawnSupplyMonsters(runtime, zone, want, includeCore, phaseIndex, now, roster);
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

    private int ConsumeSupplyStoneBudget(MatchRuntime runtime, Monster monster, DateTime now)
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

    private int SpawnSupplyMonsters(MatchRuntime runtime, AreaType area, int normals, bool includeCore, int phaseIndex, DateTime now, IReadOnlyList<long> roster)
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
        }

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
