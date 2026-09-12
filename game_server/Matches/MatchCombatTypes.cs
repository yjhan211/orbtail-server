using network.common;
using network.common.data.models;

namespace game_server.matches;

public readonly record struct PlayerPositionSnapshot(
    long PlayerId,
    AreaType Area,
    Vector3f Position);

public readonly record struct MonsterContactDamage(
    int MonsterId,
    long TargetPlayerId,
    AreaType Area,
    int Damage);

public readonly record struct PendingMonsterHit(
    long CombatTargetId,
    long AttackerId,
    int Damage,
    DateTime ApplyAtUtc);

public readonly record struct PendingWaveAttack(
    long OwnerId,
    AreaType Area,
    Vector3f Position,
    int Damage,
    float Radius,
    int SourceItemId,
    DateTime ExplodeAtUtc);

public readonly record struct ProximityCombatActor(
    long PlayerId,
    AreaType Area,
    Vector3f Position,
    int WeaponItemId,
    float AttackRange,
    int Damage,
    float AttackIntervalSeconds,
    MapId MapId = MapId.None,
    Cell? Cell = null,
    long WeaponItemUid = 0,
    int WeaponStackIndex = 0,
    bool IsMonsterTarget = false,
    int TargetPriority = 0,
    bool Untargetable = false,
    int TrailOrdinal = 0);

public readonly record struct ProximityCombatAttack(
    long AttackerPlayerId,
    long TargetPlayerId,
    AreaType Area,
    int WeaponItemId,
    int Damage,
    int CandidateTargetCount = 0,
    long AttackerItemUid = 0,
    Vector3f? Origin = null,
    Vector3f? AnchorPosition = null,
    int AttackerTrailOrdinal = 0);

public sealed class CutRetaliationWindow
{
    public DateTime ExpiresAtUtc;
    public int BlockedDamage;
    public int BlockedHits;
    public int BlockedCuts;
    public bool Retaliated;
    public AreaType OpenedArea;
}

/// <summary>잠긴 교차사격 직선 하나. 앞머리 위치와 맞은 대상 집합은 매치 잠금 안에서 틱마다 전진한다.</summary>
public sealed class SwarmCrossfireShape
{
    public long EventId { get; init; }
    public long OwnerId { get; init; }
    public int WeaponItemId { get; init; }
    public int Damage { get; init; }
    public AreaType Area { get; init; }
    public Vector3f Origin { get; init; } = new(0f, 0f, 0f);
    public Vector3f End { get; init; } = new(0f, 0f, 0f);
    public float GroundLength { get; init; }
    public float HalfWidth { get; init; }
    public float BlastRadius { get; init; }
    public float SweepSpeed { get; init; }
    public DateTime ArmedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public bool DetonateAtWall { get; init; }
    public int AnchorMonsterId { get; init; }
    public long AnchorCombatTargetId { get; init; }
    public float LastFront { get; set; }
    public HashSet<long> HitVictims { get; } = new();
    public HashSet<long> HitMonsters { get; } = new();
}
