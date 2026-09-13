using network.common;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>
///     매치 하나의 몬스터 상태. MatchRuntime이 소유하고 매치 잠금 안에서만 읽고 쓴다.
///     개체 목록·공급 구역 회계·침투 경로 캐시를 보관한다.
///     초기화·조회·죽은 개체 정리를 맡고, 공급·전투·이동 판단은 각 서비스가 수행한다.
///     매치 정리 때 Release로 비운다.
/// </summary>
public sealed class MatchMonsters
{
    private const double DeadPruneAfterSeconds = 3d;
    public bool IsInitialized { get; set; }
    public DateTime NextSnapshotAtUtc { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime LastTickAtUtc { get; set; }
    public Dictionary<int, Monster> Entities { get; } = new();
    public Random Rng { get; set; } = new();
    public int NextSerial { get; set; }
    public Dictionary<AreaType, float> InfiltrationExitBearings { get; } = new();
    public Dictionary<AreaType, MonsterSupplyZoneState> SupplyZones { get; } = new();
    public Dictionary<AreaType, DateTime> ZoneVacatedAtUtc { get; } = new();
    public Dictionary<(AreaType Area, int PhaseIndex), (double Available, DateTime RefilledAtUtc)> SupplyStoneBucket { get; } = new();
    public HashSet<(AreaType Area, int PhaseIndex)> SupplyCoreRewarded { get; } = new();
    public int NextSupplyPackOrdinal { get; set; }
    public int NextInfiltrationOriginOrdinal { get; set; }
    public int MaxParticipantCount { get; set; }

    public bool Initialize(DateTime startsAtUtc)
    {
        if (IsInitialized)
        {
            return false;
        }

        StartsAtUtc = startsAtUtc;
        LastTickAtUtc = startsAtUtc;
        Rng = new Random(unchecked((int)(startsAtUtc.Ticks ^ 0x5A7A_17)));
        IsInitialized = true;
        return true;
    }

    public IReadOnlyDictionary<AreaType, List<MonsterInfo>> GetVisualStatesByArea()
    {
        var statesByArea = new Dictionary<AreaType, List<MonsterInfo>>();
        foreach (var monster in Entities.Values)
        {
            if (monster.MonsterId <= 0 || monster.Area == AreaType.None)
            {
                continue;
            }
            if (!statesByArea.TryGetValue(monster.Area, out var areaStates))
            {
                areaStates = new List<MonsterInfo>();
                statesByArea[monster.Area] = areaStates;
            }
            areaStates.Add(monster.ToMonsterInfo());
        }
        foreach (var areaStates in statesByArea.Values)
        {
            areaStates.Sort(static (left, right) => left.MonsterId.CompareTo(right.MonsterId));
        }
        return statesByArea;
    }

    public IReadOnlyList<Monster> GetCombatTargets(DateTime nowUtc)
    {
        var targets = new List<Monster>();
        foreach (var monster in Entities.Values)
        {
            if (!monster.Alive || nowUtc < monster.ActivatesAtUtc || monster.Health <= monster.PendingDamage)
            {
                continue;
            }
            targets.Add(monster);
        }
        return targets;
    }

    public Monster? FindByCombatTarget(long combatTargetId)
    {
        foreach (var candidate in Entities.Values)
        {
            if (candidate.CombatTargetId == combatTargetId)
            {
                return candidate;
            }
        }
        return null;
    }

    public Monster? FindAliveByCombatTarget(long combatTargetId)
    {
        var monster = FindByCombatTarget(combatTargetId);
        return monster is { Alive: true } ? monster : null;
    }

    public void RemoveExpiredDead(DateTime now)
    {
        var expired = new List<int>();
        foreach (var monster in Entities.Values)
        {
            if (!monster.Alive && (now - monster.DiedAtUtc).TotalSeconds > DeadPruneAfterSeconds)
            {
                expired.Add(monster.MonsterId);
            }
        }
        foreach (int monsterId in expired)
        {
            Entities.Remove(monsterId);
        }
    }

    internal void Release()
    {
        IsInitialized = false;
        NextSnapshotAtUtc = default;
        Entities.Clear();
        InfiltrationExitBearings.Clear();
        SupplyZones.Clear();
        ZoneVacatedAtUtc.Clear();
        SupplyStoneBucket.Clear();
        SupplyCoreRewarded.Clear();
    }
}

public sealed class MonsterSupplyZoneState
{
    public DateTime? NextTopUpAtUtc { get; set; }
    public DateTime? WipeRestUntilUtc { get; set; }
    public bool HasSpawned { get; set; }
}
