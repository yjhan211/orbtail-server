using network.common;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>
///     매치 하나의 몬스터 상태. MatchRuntime이 소유하고 매치 잠금 안에서만 읽고 쓴다.
///     개체 목록·접촉 면역·공급 구역 회계·계측을 들며, 공급·추격·피해 규칙은 MatchMonsterService가 처리한다.
/// </summary>
public sealed class MatchMonsterState
{
    private bool _initialized;
    public long HumanPlayerId { get; private set; }
    public DateTime StartsAtUtc { get; private set; }
    public DateTime LastTickAtUtc { get; set; }
    public Dictionary<int, Monster> Entities { get; } = new();
    public Dictionary<long, DateTime> ContactImmuneUntilUtc { get; } = new();
    public Random Rng { get; private set; } = new();
    public int NextSerial { get; set; }
    public bool NextMonsterGrantsSummonStone { get; set; } = true;
    public PlayerPositionSnapshot[] LastParticipants { get; set; } = [];
    public int HitsTaken { get; set; }
    public int Kills { get; set; }
    public Dictionary<MonsterInsignia, int> InsigniaHits { get; } = new();
    public Dictionary<AreaType, float> InfiltrationExitBearings { get; } = new();
    public Dictionary<AreaType, MonsterSupplyZoneState> SupplyZones { get; } = new();
    public Dictionary<AreaType, DateTime> ZoneVacatedAtUtc { get; } = new();
    public Dictionary<(AreaType Area, int PhaseIndex), (double Available, DateTime RefilledAtUtc)> SupplyStoneBucket { get; } = new();
    public HashSet<(AreaType Area, int PhaseIndex)> SupplyCoreRewarded { get; } = new();
    public int NextSupplyPackOrdinal { get; set; }
    public int NextInfiltrationOriginOrdinal { get; set; }
    public int MaxParticipantCount { get; set; }
    public DateTime NextMonsterPositionBroadcastAtUtc { get; set; }

    public bool HasMatching() => _initialized;
    public bool InitializeMatching(long humanPlayerId, DateTime startsAtUtc)
    {
        if (humanPlayerId == 0 || _initialized)
        {
            return false;
        }

        HumanPlayerId = humanPlayerId;
        StartsAtUtc = startsAtUtc;
        LastTickAtUtc = startsAtUtc;
        Rng = new Random(unchecked((int)(startsAtUtc.Ticks ^ humanPlayerId ^ 0x5A7A_17)));
        _initialized = true;
        return true;
    }

    internal void Release()
    {
        _initialized = false;
        Entities.Clear();
        ContactImmuneUntilUtc.Clear();
        LastParticipants = [];
        InsigniaHits.Clear();
        InfiltrationExitBearings.Clear();
        SupplyZones.Clear();
        ZoneVacatedAtUtc.Clear();
        SupplyStoneBucket.Clear();
        SupplyCoreRewarded.Clear();
    }

    public bool HasWaveInsignia(int monsterId) => Entities.TryGetValue(monsterId, out var monster) && monster.Insignia == MonsterInsignia.Wave;

    private Monster? FindAliveByCombatTarget(long combatTargetId)
    {
        foreach (var candidate in Entities.Values)
        {
            if (candidate.CombatTargetId == combatTargetId && candidate.Alive)
            {
                return candidate;
            }
        }
        return null;
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

    public IReadOnlyList<MonsterRuntimeInfo> GetVisualStates()
    {
        var states = new List<MonsterRuntimeInfo>(Entities.Count);
        foreach (var monster in Entities.Values)
        {
            states.Add(monster.ToMonsterRuntimeInfo());
        }
        return states;
    }

    public IReadOnlyList<MonsterCombatTarget> GetCombatTargets(DateTime nowUtc)
    {
        var targets = new List<MonsterCombatTarget>();
        foreach (var monster in Entities.Values)
        {
            if (!monster.Alive || nowUtc < monster.ActivatesAtUtc || monster.Health <= monster.PendingDamage)
            {
                continue;
            }
            targets.Add(new MonsterCombatTarget(monster.CombatTargetId, monster.Area, monster.Position, monster.MonsterId));
        }
        return targets;
    }

    public int GetMonsterIdForCombatTarget(long combatTargetId) => FindAliveByCombatTarget(combatTargetId)?.MonsterId ?? 0;

    public void TrySlowMonster(long combatTargetId, float slowSeconds, DateTime nowUtc)
    {
        var monster = FindAliveByCombatTarget(combatTargetId);
        if (monster == null)
        {
            return;
        }
        monster.WaveSlowUntilUtc = nowUtc.AddSeconds(Math.Max(0f, slowSeconds));
    }

    public MonsterSummary GetSummary()
    {
        var insigniaHits = new Dictionary<string, int>();
        foreach (var (insignia, hits) in InsigniaHits)
        {
            insigniaHits[insignia.ToString()] = hits;
        }
        return new MonsterSummary(HitsTaken, Kills, insigniaHits);
    }
}

public sealed class MonsterSupplyZoneState
{
    public DateTime? NextTopUpAtUtc { get; set; }
    public DateTime? WipeRestUntilUtc { get; set; }
    public bool HasSpawned { get; set; }
}
