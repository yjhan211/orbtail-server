using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

public readonly record struct MonsterContactDamage(
    int MonsterId,
    long TargetPlayerId,
    AreaType Area,
    int Damage);

public readonly record struct PendingMonsterHit(
    int MonsterId,
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
    DateTime ExplodeAtUtc,
    bool AppliesSlow,
    bool IsPublished = false);

public sealed class CutRetaliationWindow
{
    public DateTime ExpiresAtUtc;
}

/// <summary>잠긴 교차사격 직선 하나. 앞머리 위치와 맞은 대상 집합은 매치 잠금 안에서 틱마다 전진한다.</summary>
public sealed class SwarmCrossfireShape
{
    public long EventId { get; init; }
    public long OwnerId { get; init; }
    public int WeaponItemId { get; init; }
    public int Damage { get; init; }
    public AreaType Area { get; init; }
    public Cell OriginCell { get; init; } = new(0, 0);
    public Cell EndCell { get; init; } = new(0, 0);
    public Vector3f Origin => MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, OriginCell);
    public Vector3f End => MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, EndCell);
    public float GroundLength => GroundGeometry.GroundDistance(Origin, End);
    public float HalfWidth => Config.TierValue(Config.SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER, OrbData.TryGetOrbGroupAndTier(WeaponItemId, out _, out int tier) ? tier : 1) * 0.5f;
    public DateTime ArmedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public bool DetonateAtEnd { get; init; }
    public int OwnerOrbOrdinal { get; init; }
    public bool IsPublished { get; set; }
    public (ObjectType Type, long Id) AnchorTarget { get; init; }
    private float? _lastFront;
    public float LastFront { get => _lastFront ?? -HalfWidth; set => _lastFront = value; }
    public HashSet<long> HitVictims { get; } = new();
    public HashSet<int> HitMonsters { get; } = new();
}
