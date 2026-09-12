using network.common;
using network.common.data.models;

namespace game_server.matches.monsters;

public enum MonsterInsignia
{
    Sun = 0,
    Wind = 1,
    Wave = 2
}

public enum MonsterKind
{
    Skeleton = 0,
    DartGoblin = 1,
    RunawayGoblin = 2,
    Bowler = 3
}

public sealed class MonsterTickResult
{
    public List<MonsterContactDamage> PlayerDamage { get; } = new();
    public List<MonsterRuntimeInfo> SpawnedMonsters { get; } = new();
    public List<SupplyPackSpawnInfo> SupplyPackSpawns { get; } = new();
}

public readonly record struct SupplyPackSpawnInfo(AreaType Area, int PackIndex, int MonsterCount, int StoneTotal);

public readonly record struct MonsterDamageResult(
    bool Applied,
    bool Killed,
    int MonsterId,
    MonsterRuntimeInfo? MonsterState,
    int HeartReward = 0,
    int BootsReward = 0,
    int KeyReward = 0,
    MonsterKind Kind = MonsterKind.Skeleton,
    double AliveSeconds = 0d,
    int AttackEventCount = 0)
{
    public static MonsterDamageResult None => new(false, false, 0, null);
}

public readonly record struct MonsterCombatTarget(
    long CombatTargetId,
    AreaType Area,
    Vector3f Position,
    int MonsterId);

public readonly record struct MonsterSummary(
    int HitsTaken,
    int Kills,
    IReadOnlyDictionary<string, int> InsigniaHits)
{
    public static MonsterSummary Empty => new(0, 0, new Dictionary<string, int>());
}
