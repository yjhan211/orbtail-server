using network.common;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>
///     매치 하나의 몬스터 상태. MatchRuntime이 소유하고 매치 잠금 안에서만 읽고 쓴다.
///     개체 목록·접촉 면역·공급 구역 회계를 들며, 공급·추격·피해 규칙은 MatchMonsterService가 처리한다.
/// </summary>
public sealed class MatchMonsterState
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(100);
    private bool _initialized;
    private DateTime _nextSnapshotAtUtc;
    public DateTime StartsAtUtc { get; private set; }
    public DateTime LastTickAtUtc { get; set; }
    public Dictionary<int, Monster> Entities { get; } = new();
    public Dictionary<long, DateTime> ContactImmuneUntilUtc { get; } = new();
    public Random Rng { get; private set; } = new();
    public int NextSerial { get; set; }
    public bool NextMonsterGrantsSummonStone { get; set; } = true;
    public PlayerPositionSnapshot[] LastParticipants { get; set; } = [];
    public Dictionary<AreaType, float> InfiltrationExitBearings { get; } = new();
    public Dictionary<AreaType, MonsterSupplyZoneState> SupplyZones { get; } = new();
    public Dictionary<AreaType, DateTime> ZoneVacatedAtUtc { get; } = new();
    public Dictionary<(AreaType Area, int PhaseIndex), (double Available, DateTime RefilledAtUtc)> SupplyStoneBucket { get; } = new();
    public HashSet<(AreaType Area, int PhaseIndex)> SupplyCoreRewarded { get; } = new();
    public int NextSupplyPackOrdinal { get; set; }
    public int NextInfiltrationOriginOrdinal { get; set; }
    public int MaxParticipantCount { get; set; }

    public bool IsInitialized => _initialized;

    public bool TryClaimSnapshotSlot(DateTime nowUtc)
    {
        if (nowUtc < _nextSnapshotAtUtc)
        {
            return false;
        }
        _nextSnapshotAtUtc = nowUtc + SnapshotInterval;
        return true;
    }
    public bool Initialize(DateTime startsAtUtc)
    {
        if (_initialized)
        {
            return false;
        }

        StartsAtUtc = startsAtUtc;
        LastTickAtUtc = startsAtUtc;
        Rng = new Random(unchecked((int)(startsAtUtc.Ticks ^ 0x5A7A_17)));
        _initialized = true;
        return true;
    }

    internal void Release()
    {
        _initialized = false;
        _nextSnapshotAtUtc = default;
        Entities.Clear();
        ContactImmuneUntilUtc.Clear();
        LastParticipants = [];
        InfiltrationExitBearings.Clear();
        SupplyZones.Clear();
        ZoneVacatedAtUtc.Clear();
        SupplyStoneBucket.Clear();
        SupplyCoreRewarded.Clear();
    }

    public bool HasWaveInsignia(int monsterId) => Entities.TryGetValue(monsterId, out var monster) && monster.Insignia == MonsterInsignia.Wave;

    /// <summary>전투 대상 ID로 개체를 찾는 유일한 경로. 죽은 개체도 돌려주므로 생사는 호출자가 본다.</summary>
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

    private Monster? FindAliveByCombatTarget(long combatTargetId)
    {
        var monster = FindByCombatTarget(combatTargetId);
        return monster is { Alive: true } ? monster : null;
    }

    public void ReserveMonsterDamage(long combatTargetId, int damage)
    {
        if (damage <= 0)
        {
            return;
        }
        var monster = FindAliveByCombatTarget(combatTargetId);
        if (monster != null)
        {
            monster.PendingDamage += damage;
        }
    }

    public void RecordMonsterAttackEvent(long combatTargetId)
    {
        var monster = FindAliveByCombatTarget(combatTargetId);
        if (monster != null)
        {
            monster.AttackEventCount++;
        }
    }

    /// <summary>구역별 스냅샷 전송용. 구역이 없는 개체는 빼고, 구역 안은 몬스터 ID순으로 정렬한다.</summary>
    public IReadOnlyDictionary<AreaType, List<MonsterRuntimeInfo>> GetVisualStatesByArea()
    {
        var statesByArea = new Dictionary<AreaType, List<MonsterRuntimeInfo>>();
        foreach (var monster in Entities.Values)
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

    public int GetMonsterIdForCombatTarget(long combatTargetId) => FindAliveByCombatTarget(combatTargetId)?.MonsterId ?? 0;

    public void ApplySlow(long combatTargetId, float slowSeconds, DateTime nowUtc)
    {
        var monster = FindAliveByCombatTarget(combatTargetId);
        if (monster == null)
        {
            return;
        }
        monster.WaveSlowUntilUtc = nowUtc.AddSeconds(Math.Max(0f, slowSeconds));
    }
}

public sealed class MonsterSupplyZoneState
{
    public DateTime? NextTopUpAtUtc { get; set; }
    public DateTime? WipeRestUntilUtc { get; set; }
    public bool HasSpawned { get; set; }
}
