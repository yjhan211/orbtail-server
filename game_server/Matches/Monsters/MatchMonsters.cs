using network.common;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>
///     매치 하나의 몬스터 상태. MatchRuntime이 소유하고 매치 잠금 안에서만 읽고 쓴다.
///     개체 목록·다음 경계 스폰 시각을 보관한다.
///     초기화·조회를 맡고, 공급·전투·이동 판단은 각 서비스가 수행한다.
///     매치 정리 때 Release로 비운다.
/// </summary>
public sealed class MatchMonsters
{
    public bool IsInitialized { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public Dictionary<int, Monster> Entities { get; } = new();
    public Random Rng { get; set; } = new();
    public int NextSerial { get; set; }
    public int NextSupplyPackOrdinal { get; set; }
    public DateTime NextSpawnAtUtc { get; set; }

    public bool Initialize(DateTime startsAtUtc)
    {
        if (IsInitialized)
        {
            return false;
        }

        StartsAtUtc = startsAtUtc;
        NextSpawnAtUtc = startsAtUtc;
        Rng = new Random(unchecked((int)(startsAtUtc.Ticks ^ 0x5A7A_17)));
        IsInitialized = true;
        return true;
    }

    public IReadOnlyDictionary<AreaType, List<MonsterInfo>> GetVisualStatesByArea()
    {
        var statesByArea = new Dictionary<AreaType, List<MonsterInfo>>();
        foreach (var monster in Entities.Values)
        {
            if (monster.MonsterId <= 0 || monster.CurrentArea == AreaType.None)
            {
                continue;
            }
            if (!statesByArea.TryGetValue(monster.CurrentArea, out var areaStates))
            {
                areaStates = new List<MonsterInfo>();
                statesByArea[monster.CurrentArea] = areaStates;
            }
            areaStates.Add(monster.ToMonsterInfo());
        }
        foreach (var areaStates in statesByArea.Values)
        {
            areaStates.Sort(static (left, right) => left.MonsterId.CompareTo(right.MonsterId));
        }
        return statesByArea;
    }

    public IReadOnlyList<Monster> GetCombatTargets()
    {
        var targets = new List<Monster>();
        foreach (var monster in Entities.Values)
        {
            if (!monster.Alive)
            {
                continue;
            }
            targets.Add(monster);
        }
        return targets;
    }

    public Monster? Find(int monsterId) => Entities.GetValueOrDefault(monsterId);

    public Monster? FindAlive(int monsterId) => Find(monsterId) is { Alive: true } monster ? monster : null;

    internal void Release()
    {
        IsInitialized = false;
        NextSpawnAtUtc = DateTime.MinValue;
        Entities.Clear();
    }
}
