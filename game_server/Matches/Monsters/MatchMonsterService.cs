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
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(100);
    private const double DeadPruneAfterSeconds = 3d;
    private const int CampsPerArea = 3;
    private const double InfiltrationGoldenAngle = 0.6180339887498949d;
    private const int FirstMonsterId = 7_000_000;
    private const long FirstCombatTargetId = -4_000_000_000_000_000_000L;
    private const int AnchorAreaEdgeMargin = 3;
    private static readonly int InsigniaCount = Enum.GetValues<MonsterInsignia>().Length;

    public static bool IsCombatTargetId(long actorId) => actorId < CombatTargetIdUpperBound;

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
        var snapshot = new PlayerPositionSnapshot[participants.Count];
        int snapshotIndex = 0;
        foreach (var participant in participants)
        {
            snapshot[snapshotIndex++] = participant;
        }

        bool preMatch = !isGameplayActive;
        double moveDeltaSeconds = (now - state.StartsAtUtc).TotalSeconds >= Config.SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS
            ? deltaSeconds * Config.SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER
            : deltaSeconds;

        ProcessSupply(runtime, snapshot, now, preMatch);
        foreach (var monster in state.Entities.Values)
        {
            if (!monster.Alive || now < monster.ActivatesAtUtc)
            {
                continue;
            }

            movement.Move(runtime, monster, snapshot, now, moveDeltaSeconds, preMatch);
            movement.RescueMonsterFromBlockedCell(monster);
            if (now < monster.NextContactAtUtc)
            {
                continue;
            }

            float attackRange = monster.Aggro && monster.AttackRangeValue > Monster.BaseContactRadius ? monster.AttackRangeValue : Monster.GetContactRadius(monster.Kind);
            const float verticalScale = 2f;
            foreach (var participant in snapshot)
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

                var player = runtime.GetParticipant(participant.PlayerId);
                if (player == null || now < player.MonsterContactImmuneUntilUtc)
                {
                    continue;
                }

                monster.NextContactAtUtc = now.AddSeconds(monster.AttackCooldownValue);
                player.MonsterContactImmuneUntilUtc = now.AddSeconds(Config.SWARM_MONSTER_CONTACT_IMMUNITY_SECONDS);
                monster.Aggro = true;
                monster.ChaseTargetPlayerId = participant.PlayerId;
                contacts.Add(new MonsterContactDamage(monster.MonsterId, participant.PlayerId, monster.Area, monster.ContactDamageValue));
                bool waveInsignia = monster.Insignia == MonsterInsignia.Wave;
                if (waveInsignia || monster.Kind == MonsterKind.Bowler)
                {
                    float splashRadius = waveInsignia ? Config.SWARM_MONSTER_WAVE_SPLASH_RADIUS : Config.SWARM_MONSTER_BOWLER_SPLASH_RADIUS;
                    foreach (var splashed in snapshot)
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

                        var splashedPlayer = runtime.GetParticipant(splashed.PlayerId);
                        if (splashedPlayer == null || now < splashedPlayer.MonsterContactImmuneUntilUtc)
                        {
                            continue;
                        }
                        splashedPlayer.MonsterContactImmuneUntilUtc = now.AddSeconds(Config.SWARM_MONSTER_CONTACT_IMMUNITY_SECONDS);
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

        var monster = FindByCombatTarget(state, combatTargetId);
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
        var phases = SwarmSupplyPhaseData.GetAll();
        for (int index = 0; index < phases.Count; index++)
        {
            if (elapsedSeconds < phases[index].UntilSeconds)
                return index;
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

    private void ProcessSupply(MatchRuntime runtime, IReadOnlyList<PlayerPositionSnapshot> participants, DateTime now, bool preMatch)
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

        // 생존이 적은 구역부터 채운다. 동률은 점유 순서를 유지한다.
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

    private static int CountAliveInArea(MatchMonsterState state, AreaType area)
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

        bool infiltrate = area != Config.SWARM_MATCH_GROUND_AREA;
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

        var inward = MapCoordinateConverter.GetAreaDirection(Config.SWARM_MATCH_MAP, area, Config.SWARM_MATCH_GROUND_AREA);
        // 화면 밖 앵커 우선, 그다음 안쪽 방향, 그다음 참가자에게서 먼 순. 침투가 아니면 안전 거리 안의 앵커는 뺀다. 동률은 앵커 순서를 유지한다.
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
            return 0;
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
            // 압박 필드 거리 기준으로 스폰 셀(안전 거리 바깥 띠)과 앵커 셀(구역 안쪽 띠)을 고른다.
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
            if (fieldSpawn != null)
            {
                position = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, fieldSpawn.Value.Spawn), packAnchor, area);
                var fieldAnchorWorld = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, fieldSpawn.Value.Anchor);
                destination = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, new Vector3f(fieldAnchorWorld.X + MathF.Cos(angle) * Config.SWARM_MONSTER_SUPPLY_SCATTER_RADIUS, fieldAnchorWorld.Y + MathF.Sin(angle) * Config.SWARM_MONSTER_SUPPLY_SCATTER_RADIUS, 0f), fieldAnchorWorld, area);
            }
            else if (infiltrate && TryPlanInfiltration(runtime, area, destination, out var origin, out var planned))
            {
                position = origin;
                spawnArea = Config.SWARM_MATCH_GROUND_AREA;
                route = planned;
            }

            var kind = spawnPlan[index].Kind;
            var definition = SwarmMonsterData.Get((int)kind) ?? SwarmMonsterData.Get((int)MonsterKind.Skeleton)
                ?? throw new InvalidOperationException("swarm_monster.csv must define the skeleton fallback row.");
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
                OwnerPlayerId = ClaimOwner()
            };

            if (route != null)
            {
                monster.Infiltrating = true;
                monster.MarchWaypoints.AddRange(route);
                monster.MarchBudgetSeconds = MonsterMovementService.ComputeMarchBudgetSeconds(position, route);
                monster.MarchLaneOffset = (float)(state.Rng.NextDouble() * 2d - 1d) * Config.SWARM_MONSTER_MARCH_LANE_OFFSET_MAX;
                monster.MarchSpeedScale = 1f + (float)(state.Rng.NextDouble() * 2d - 1d) * Config.SWARM_MONSTER_MARCH_SPEED_JITTER;
            }

            state.Entities[monster.MonsterId] = monster;
        }

        return spawnPlan.Count;
    }

    private bool TryPlanInfiltration(MatchRuntime runtime, AreaType destinationArea, Vector3f destination, out Vector3f origin, out List<Vector3f> route)
    {
        route = null!;
        var originCenter = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, Config.SWARM_MATCH_GROUND_AREA));
        var state = runtime.Monsters;
        float baseAngle = ResolveInfiltrationExitBearing(runtime, destinationArea, destination, originCenter);
        double golden = (state.NextInfiltrationOriginOrdinal++ * InfiltrationGoldenAngle) % 1d;
        float angle = baseAngle + (float)((golden - 0.5d) * 2d) * Config.SWARM_MONSTER_INFILTRATION_ORIGIN_JITTER_RADIANS;
        origin = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, new Vector3f(originCenter.X + MathF.Cos(angle) * Config.SWARM_MONSTER_INFILTRATION_ORIGIN_RADIUS, originCenter.Y + MathF.Sin(angle) * Config.SWARM_MONSTER_INFILTRATION_ORIGIN_RADIUS, 0f), originCenter, Config.SWARM_MATCH_GROUND_AREA);

        var burst = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, new Vector3f(origin.X + MathF.Cos(angle) * Config.SWARM_MONSTER_INFILTRATION_BURST_DISTANCE, origin.Y + MathF.Sin(angle) * Config.SWARM_MONSTER_INFILTRATION_BURST_DISTANCE, 0f), originCenter, Config.SWARM_MATCH_GROUND_AREA);
        if (MapPathfinder.IsSegmentWalkable(Config.SWARM_MATCH_MAP, origin, burst) && MapPathfinder.TryPlanRoute(Config.SWARM_MATCH_MAP, Config.SWARM_MATCH_GROUND_AREA, burst, destinationArea, destination, candidate => IsInfiltrationRouteBlocked(runtime, candidate, destinationArea), out route))
        {
            route.Insert(0, burst);
            return true;
        }

        return MapPathfinder.TryPlanRoute(Config.SWARM_MATCH_MAP, Config.SWARM_MATCH_GROUND_AREA, origin, destinationArea, destination, candidate => IsInfiltrationRouteBlocked(runtime, candidate, destinationArea), out route);
    }

    private float ResolveInfiltrationExitBearing(MatchRuntime runtime, AreaType destinationArea, Vector3f destination, Vector3f originCenter)
    {
        var bearings = runtime.Monsters.InfiltrationExitBearings;
        if (bearings.TryGetValue(destinationArea, out float cached))
        {
            return cached;
        }

        float fallback = MathF.Atan2(destination.Y - originCenter.Y, destination.X - originCenter.X);
        if (!MapPathfinder.TryPlanRoute(Config.SWARM_MATCH_MAP, Config.SWARM_MATCH_GROUND_AREA, originCenter, destinationArea, destination, candidate => IsInfiltrationRouteBlocked(runtime, candidate, destinationArea), out var probe))
        {
            return fallback;
        }

        float exitRadius = Config.SWARM_MONSTER_INFILTRATION_ORIGIN_RADIUS + Config.SWARM_MONSTER_INFILTRATION_BURST_DISTANCE;
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

    private bool IsInfiltrationRouteBlocked(MatchRuntime runtime, AreaType candidate, AreaType destinationArea)
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

    public bool Initialize(MatchRuntime runtime, DateTime startsAtUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var state = runtime.Monsters;
        if (state.IsInitialized)
        {
            return false;
        }

        state.StartsAtUtc = startsAtUtc;
        state.LastTickAtUtc = startsAtUtc;
        state.Rng = new Random(unchecked((int)(startsAtUtc.Ticks ^ 0x5A7A_17)));
        state.IsInitialized = true;
        return true;
    }

    /// <summary>100ms 스냅샷 슬롯을 한 번만 내준다. 같은 슬롯 안의 재호출은 거절한다.</summary>
    public bool TryClaimSnapshotSlot(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var state = runtime.Monsters;
        if (nowUtc < state.NextSnapshotAtUtc)
        {
            return false;
        }
        state.NextSnapshotAtUtc = nowUtc + SnapshotInterval;
        return true;
    }

    /// <summary>구역별 스냅샷 전송용. 구역이 없는 개체는 빼고, 구역 안은 몬스터 ID순으로 정렬한다.</summary>
    public IReadOnlyDictionary<AreaType, List<MonsterRuntimeInfo>> GetVisualStatesByArea(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var statesByArea = new Dictionary<AreaType, List<MonsterRuntimeInfo>>();
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            if (monster.MonsterId <= 0 || monster.Area == AreaType.None)
            {
                continue;
            }
            if (!statesByArea.TryGetValue(monster.Area, out var areaStates))
            {
                areaStates = new List<MonsterRuntimeInfo>();
                statesByArea[monster.Area] = areaStates;
            }
            areaStates.Add(monster.ToMonsterRuntimeInfo());
        }
        foreach (var areaStates in statesByArea.Values)
        {
            areaStates.Sort(static (left, right) => left.MonsterId.CompareTo(right.MonsterId));
        }
        return statesByArea;
    }

    /// <summary>공격 대상이 될 수 있는 개체. 살아 있고 활성화됐으며 예약 피해로 이미 죽을 개체는 뺀다.</summary>
    public IReadOnlyList<Monster> GetCombatTargets(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var targets = new List<Monster>();
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            if (!monster.Alive || nowUtc < monster.ActivatesAtUtc || monster.Health <= monster.PendingDamage)
            {
                continue;
            }
            targets.Add(monster);
        }
        return targets;
    }

    public int GetMonsterIdForCombatTarget(MatchRuntime runtime, long combatTargetId)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        return FindAliveByCombatTarget(runtime.Monsters, combatTargetId)?.MonsterId ?? 0;
    }

    /// <summary>발사 순간 피해를 미리 물려 다른 오브가 같은 개체를 또 고르지 않게 한다. 실제 적용은 ApplyMonsterDamage.</summary>
    public void ReserveMonsterDamage(MatchRuntime runtime, long combatTargetId, int damage)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        if (damage <= 0)
        {
            return;
        }
        var monster = FindAliveByCombatTarget(runtime.Monsters, combatTargetId);
        if (monster != null)
        {
            monster.PendingDamage += damage;
        }
    }

    public void RecordMonsterAttackEvent(MatchRuntime runtime, long combatTargetId)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var monster = FindAliveByCombatTarget(runtime.Monsters, combatTargetId);
        if (monster != null)
        {
            monster.AttackEventCount++;
        }
    }

    public void ApplySlow(MatchRuntime runtime, long combatTargetId, float slowSeconds, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var monster = FindAliveByCombatTarget(runtime.Monsters, combatTargetId);
        if (monster == null)
        {
            return;
        }
        monster.WaveSlowUntilUtc = nowUtc.AddSeconds(Math.Max(0f, slowSeconds));
    }

    /// <summary>전투 대상 ID로 개체를 찾는 유일한 경로. 죽은 개체도 돌려주므로 생사는 호출자가 본다.</summary>
    private static Monster? FindByCombatTarget(MatchMonsterState state, long combatTargetId)
    {
        foreach (var candidate in state.Entities.Values)
        {
            if (candidate.CombatTargetId == combatTargetId)
            {
                return candidate;
            }
        }
        return null;
    }

    private static Monster? FindAliveByCombatTarget(MatchMonsterState state, long combatTargetId)
    {
        var monster = FindByCombatTarget(state, combatTargetId);
        return monster is { Alive: true } ? monster : null;
    }
}
