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

    public void ProcessTick(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster supply requires the match lock.");
        }
        var state = runtime.Monsters;
        if (runtime.IsEnded || runtime.Mode == MatchMode.SoloMapValidation || !state.IsInitialized || nowUtc < state.NextSpawnAtUtc)
        {
            return;
        }

        // 매 틱 재검사하지 않고 다음 공급 주기까지 대기
        state.NextSpawnAtUtc = nowUtc.AddSeconds(Config.SWARM_MONSTER_SUPPLY_TOP_UP_INTERVAL_SECONDS);

        // 정리 대기 중인 사망한 몬스터는 몬스터 수에서 제외
        int aliveCount = 0;
        foreach (var monster in state.Entities.Values)
        {
            if (monster.Alive)
            {
                aliveCount++;
            }
        }
        int spawnCount = Math.Min(Config.SWARM_MONSTER_SUPPLY_TOP_UP_COUNT, Config.SWARM_MONSTER_SUPPLY_GLOBAL_ALIVE_HARD_CAP - aliveCount);
        if (spawnCount <= 0)
        {
            return;
        }

        // 현재 자기장 경계
        double outerSpawnDistance = Math.Min(runtime.Closures.GetSafeDistance(nowUtc), SwarmPressureField.MaxDistance);
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
            state.NextSpawnAtUtc = nowUtc.AddSeconds(Config.SWARM_MONSTER_SUPPLY_BLOCKED_RETRY_SECONDS);
            return;
        }

        // 경과 시간으로 능력치 페이즈 선택
        int phaseIndex = GetSupplyPhaseIndex((nowUtc - state.StartsAtUtc).TotalSeconds);
        SpawnSupplyMonsters(runtime, spawnCells, Math.Min(spawnCount, spawnCells.Count), phaseIndex, nowUtc);
    }

    private void SpawnSupplyMonsters(MatchRuntime runtime, List<(AreaType Area, Cell Cell)> spawnCells, int count, int phaseIndex, DateTime nowUtc)
    {
        var state = runtime.Monsters;
        var phase = SwarmSupplyPhaseData.GetAll()[phaseIndex];
        var insignia = (MonsterInsignia)(state.NextSupplyPackOrdinal++ % InsigniaCount);
        for (int index = 0; index < count; index++)
        {
            // 생성할 구역 랜덤 선택
            int selectedIndex = state.Rng.Next(spawnCells.Count);
            var spawnCell = spawnCells[selectedIndex].Cell;
            spawnCells.RemoveAt(selectedIndex);

            bool isCore = phaseIndex >= Config.SWARM_MONSTER_SUPPLY_CORE_FIRST_PHASE_INDEX && state.Rng.NextDouble() < Config.SWARM_MONSTER_SUPPLY_CORE_SPAWN_CHANCE;
            var kind = isCore ? MonsterKind.RunawayGoblin : MonsterKind.Skeleton;
            var definition = SwarmMonsterData.Get((int)kind) ?? SwarmMonsterData.Get((int)MonsterKind.Skeleton);
            if (definition == null)
            {
                throw new InvalidOperationException("swarm_monster.csv must define the skeleton fallback row.");
            }

            int maxHp = isCore ? phase.CoreHp : phase.NormalHp;
            int heartReward = definition.HeartReward;
            if (heartReward <= 0 && state.Rng.NextDouble() < Config.SWARM_MONSTER_SUPPLY_HEART_DROP_CHANCE)
            {
                heartReward = 1;
            }

            var monster = new Monster
            {
                MonsterId = FirstMonsterId + state.NextSerial++,
                Insignia = insignia,
                Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, spawnCell),
                Health = maxHp,
                Alive = true,
                PhaseTier = phaseIndex,
                NextContactAtUtc = nowUtc,
                SummonStoneReward = isCore ? Config.SWARM_MONSTER_SUPPLY_CORE_STONE_REWARD : definition.StoneReward,
                HeartReward = heartReward,
                ContactDamageValue = isCore ? definition.OrbDamage : phase.ContactDamage,
                Kind = kind,
                MaxHealthValue = maxHp,
                AttackRangeValue = definition.AttackRange,
                AttackCooldownValue = definition.AttackCooldownSeconds,
                Movement =
                {
                    LastProcessedAtUtc = nowUtc
                }
            };

            state.Entities[monster.MonsterId] = monster;
        }
    }

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
}
