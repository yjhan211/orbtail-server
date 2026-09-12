using game_server.matches.combat;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

public enum SwarmPattern
{
    Ring = 0,
    Rush = 1,
    Encircle = 2
}

/// <summary>
///     #219 SB 몬스터 4종. 보스 종(골렘 4·베이비 드래곤 5·트리 자이언트 6)은 #229 구역 공급 전환 뒤
///     스폰 경로가 없어 #335에서 삭제 — 값 4~6은 클라 SwarmAfterimageMonsterDisplay 보스 휴리스틱과의
///     충돌을 피해 비워 둔다.
/// </summary>
public enum SwarmMonsterKind
{
    Skeleton = 0,
    DartGoblin = 1,
    RunawayGoblin = 2,
    Bowler = 3
}

public sealed class SwarmArenaTickResult
{
    public List<MonsterContactDamage> PlayerDamage { get; } = new();
    public List<MonsterRuntimeInfo> SpawnedMonsters { get; } = new();
    public List<SupplyPackSpawnInfo> SupplyPackSpawns { get; } = new();

    /// <summary>정지 감시 보고 (임시 진단) — GameServer가 로그로 옮겨 적는다.</summary>
    public List<string> StuckReports { get; } = new();
}

/// <summary>공급 무리 스폰 계측 (#226 E) — GameServer가 매치 이벤트 로그로 옮겨 적는다.</summary>
public readonly record struct SupplyPackSpawnInfo(
    AreaType Area, int PackIndex, int MonsterCount, int StoneTotal);

public readonly record struct SwarmArenaDamageResult(
    bool Applied,
    bool Killed,
    int MonsterId,
    MonsterRuntimeInfo? MonsterState,
    int HeartReward = 0,
    int BootsReward = 0,
    int KeyReward = 0,
    SwarmMonsterKind Kind = SwarmMonsterKind.Skeleton,
    // 기준점 계측 (#232 1단계): 처치 시점의 생존초와 살아 있는 동안 받은 공격 사건 수.
    double AliveSeconds = 0d,
    int AttackEventCount = 0)
{
    public static SwarmArenaDamageResult None => new(false, false, 0, null);
}

public readonly record struct SwarmArenaCombatTarget(
    long CombatTargetId,
    AreaType Area,
    Vector3f Position,
    int MonsterId);

public readonly record struct SwarmMonsterSummary(
    int HitsTaken,
    int Kills,
    IReadOnlyDictionary<string, int> PatternHits)
{
    public static SwarmMonsterSummary Empty => new(0, 0, new Dictionary<string, int>());
}
