using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>
///     자기장 경계에서 주기적으로 몬스터를 생성한다.
///     상태는 MatchMonsters에 보관하며, 호출자는 매치 잠금을 보유해야 한다.
/// </summary>
internal sealed class MatchMonsterSpawnService
{
    private const int FirstMonsterId = 7_000_000;
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

    public void ProcessTick(MatchRuntime runtime, DateTime now)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster supply requires the match lock.");
        }
        var state = runtime.Monsters;
        if (runtime.IsEnded || runtime.Mode == MatchMode.SoloMapValidation || !state.IsInitialized || now < state.NextSpawnAtUtc)
        {
            return;
        }

        // 매 틱 재검사하지 않고 다음 공급 주기까지 대기
        state.NextSpawnAtUtc = now.AddSeconds(Config.SWARM_MONSTER_SUPPLY_TOP_UP_INTERVAL_SECONDS);

        // 정리 대기 중인 사망한 몬스터는 몬스터 수에서 제외
        int aliveCount = 0;
        foreach (var monster in state.Entities.Values)
        {
            if (!monster.Alive)
            {
                continue;
            }
            aliveCount++;
        }
        int spawnCount = Math.Min(Config.SWARM_MONSTER_SUPPLY_TOP_UP_COUNT, Config.SWARM_MONSTER_SUPPLY_GLOBAL_ALIVE_HARD_CAP - aliveCount);
        if (spawnCount <= 0)
        {
            return;
        }

        // 현재 자기장 경계
        double outerSpawnDistance = Math.Min(runtime.Closures.GetSafeDistance(now), SwarmPressureField.MaxDistance);
        // 자기장 경계에서 조금 안쪽 - 생성 구간
        double innerSpawnDistance = Math.Max(0d, outerSpawnDistance - Config.SWARM_MONSTER_FIELD_SPAWN_BAND_CELLS);
        var spawnCells = new List<(AreaType Area, Cell Cell)>();
        foreach (var area in SwarmPressureField.GetKnownAreas())
        {
            // 폐쇄구역에는 생성하지 않음
            if (area == AreaType.None || runtime.Closures.IsAreaClosed(area))
            {
                continue;
            }
            // 중심에서 가까운 순으로 정렬
            foreach (var entry in SwarmPressureField.GetAreaCellsByDistance(area))
            {
                // 자기장 경계를 넘은 셀
                if (entry.Distance > outerSpawnDistance)
                {
                    break;
                }
                // 너무 안쪽 셀
                if (entry.Distance < innerSpawnDistance)
                {
                    continue;
                }
                spawnCells.Add((area, entry.Cell));
            }
        }
        if (spawnCells.Count == 0)
        {
            // 경계에 생성할 셀이 없으면 잠시 후 다시 시도
            state.NextSpawnAtUtc = now.AddSeconds(Config.SWARM_MONSTER_SUPPLY_BLOCKED_RETRY_SECONDS);
            return;
        }

        // 경과 시간으로 페이즈 선택 능력치 페이즈를 선택
        int phaseIndex = GetSupplyPhaseIndex((now - state.StartsAtUtc).TotalSeconds);
        SpawnSupplyMonsters(runtime, spawnCells, Math.Min(spawnCount, spawnCells.Count), phaseIndex, now);
    }

    private void SpawnSupplyMonsters(MatchRuntime runtime, List<(AreaType Area, Cell Cell)> spawnCells, int count, int phaseIndex, DateTime now)
    {
        var state = runtime.Monsters;
        var phase = SwarmSupplyPhaseData.GetAll()[phaseIndex];
        var spawnedStates = new Dictionary<AreaType, List<MonsterInfo>>();
        var insignia = (MonsterInsignia)(state.NextSupplyPackOrdinal++ % InsigniaCount);
        for (int index = 0; index < count; index++)
        {
            // 생성할 구역 랜덤 선택
            int selectedIndex = state.Rng.Next(spawnCells.Count);
            var (area, spawnCell) = spawnCells[selectedIndex];
            spawnCells.RemoveAt(selectedIndex);
            var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, spawnCell);
            bool isCore = false;
            if (Config.SWARM_MONSTER_SUPPLY_CORE_FIRST_PHASE_INDEX <= phaseIndex)
            {
                isCore = state.Rng.NextDouble() < Config.SWARM_MONSTER_SUPPLY_CORE_SPAWN_CHANCE;
            }
            var kind = isCore ? MonsterKind.RunawayGoblin : MonsterKind.Skeleton;
            var definition = SwarmMonsterData.Get((int)kind) ?? SwarmMonsterData.Get((int)MonsterKind.Skeleton);
            if (definition == null)
            {
                throw new InvalidOperationException("swarm_monster.csv must define the skeleton fallback row.");
            }
            int stoneReward = isCore ? Config.SWARM_MONSTER_SUPPLY_CORE_STONE_REWARD : definition.StoneReward;
            int maxHp = isCore ? phase.CoreHp : phase.NormalHp;
            int contactDamage = isCore ? definition.OrbDamage : phase.ContactDamage;
            int serial = state.NextSerial++;
            var monster = new Monster
            {
                MonsterId = FirstMonsterId + serial,
                Insignia = insignia,
                Position = position,
                Health = maxHp,
                Alive = true,
                PhaseTier = phaseIndex,
                NextContactAtUtc = now,
                SummonStoneReward = stoneReward,
                HeartReward = definition.HeartReward > 0 ? definition.HeartReward : state.Rng.NextDouble() < Config.SWARM_MONSTER_SUPPLY_HEART_DROP_CHANCE ? 1 : 0,
                ContactDamageValue = contactDamage,
                Kind = kind,
                MaxHealthValue = maxHp,
                AttackRangeValue = definition.AttackRange > 0f ? definition.AttackRange : Monster.BaseContactRadius,
                AttackCooldownValue = insignia == MonsterInsignia.Wave && !isCore ? Config.SWARM_MONSTER_WAVE_INSIGNIA_ATTACK_COOLDOWN_SECONDS : definition.AttackCooldownSeconds,
                Movement =
                {
                    LastProcessedAtUtc = now
                }
            };

            state.Entities[monster.MonsterId] = monster;
            if (!spawnedStates.TryGetValue(GameMapData.GetCurrentArea(monster.Info.ObjectInfo.MapId, monster.Info.ObjectInfo.Cell), out var states))
            {
                states = [];
                spawnedStates[GameMapData.GetCurrentArea(monster.Info.ObjectInfo.MapId, monster.Info.ObjectInfo.Cell)] = states;
            }
            states.Add(monster.ToMonsterInfo());
        }

        foreach (var session in runtime.GetSessions())
        {
            session.SendMonsterSnapshot(spawnedStates, fullSnapshot: false);
        }
    }
}
