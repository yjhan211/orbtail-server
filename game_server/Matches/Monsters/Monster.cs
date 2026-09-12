using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>몬스터 한 개체의 상태와 피해·감속·공격 예약 변경을 담당한다. 매치 잠금 안에서 사용한다.</summary>
public sealed class Monster
{
    public static float BaseContactRadius => Config.SWARM_MONSTER_BASE_CONTACT_RADIUS;

    public static float GetContactRadius(MonsterKind kind) => BaseContactRadius * (SwarmMonsterData.Get((int)kind)?.ContactRadiusScale ?? 1f);

    public int MonsterId { get; init; }
    public long CombatTargetId { get; init; }
    public MonsterInsignia Insignia { get; init; }
    public AreaType Area { get; set; }
    public Vector3f Position { get; set; } = new(0f, 0f, 0f);
    public int Health { get; set; }
    public bool Alive { get; set; }
    public DateTime ActivatesAtUtc { get; set; }
    public DateTime NextContactAtUtc { get; set; }
    public DateTime DiedAtUtc { get; set; }
    public DateTime SpawnedAtUtc { get; set; }
    public float ScatterAngle { get; init; }
    public DateTime WaveSlowUntilUtc { get; set; }
    public int SummonStoneReward { get; init; }
    public int HeartReward { get; init; }
    public int ContactDamageValue { get; init; }
    public long ChaseTargetPlayerId { get; set; }
    public MonsterKind Kind { get; init; } = MonsterKind.Skeleton;
    public int MaxHealthValue { get; init; }
    public float AttackRangeValue { get; init; }
    public float AttackCooldownValue { get; init; }
    public DateTime NextTargetScanAtUtc { get; set; }
    public float AnchorX { get; set; }
    public float AnchorY { get; set; }
    public bool Aggro { get; set; }
    public AreaType HomeArea { get; set; }
    public int PhaseTier { get; set; }
    public long OwnerPlayerId { get; set; }
    public int PendingDamage { get; set; }
    public bool Infiltrating { get; set; }
    public bool MarchIsPursuit { get; set; }
    public List<Vector3f> MarchWaypoints { get; } = new();
    public int MarchIndex { get; set; }
    public double MarchBudgetSeconds { get; set; }
    public float MarchLaneOffset { get; set; }
    public float MarchSpeedScale { get; set; } = 1f;
    public DateTime NextChasePlanAtUtc { get; set; }

    public static bool IsCombatTargetId(long actorId) => actorId < -1_000_000_000_000L;

    public void ReserveDamage(int damage)
    {
        if (Alive && damage > 0)
        {
            PendingDamage += damage;
        }
    }

    // 이미 죽었더라도 도착한 지연 타격의 예약량은 해제한다.
    public void ReleaseReservedDamage(int damage)
    {
        if (damage > 0)
        {
            PendingDamage = Math.Max(0, PendingDamage - damage);
        }
    }

    public bool ApplyDamage(int damage, DateTime nowUtc)
    {
        if (!Alive || damage <= 0)
        {
            return false;
        }
        Health = Math.Max(0, Health - damage);
        if (Health != 0)
        {
            return false;
        }
        Alive = false;
        DiedAtUtc = nowUtc;
        return true;
    }

    public void ApplySlow(float slowSeconds, DateTime nowUtc)
    {
        if (Alive)
        {
            WaveSlowUntilUtc = nowUtc.AddSeconds(Math.Max(0f, slowSeconds));
        }
    }

    public MonsterRuntimeInfo ToMonsterRuntimeInfo() => new()
    {
        MonsterId = MonsterId,
        AreaType = Area,
        PositionX = Position.X,
        PositionY = Position.Y,
        MaxHealth = MaxHealthValue,
        CurrentHealth = Health,
        IsAlive = Alive,
        RewardItemId = Insignia switch
        {
            MonsterInsignia.Sun => 107000010,
            MonsterInsignia.Wind => 107000020,
            _ => 107000030
        },
        IsCore = false,
        Phase = PhaseTier,
        SummonStoneReward = SummonStoneReward,
        ChaseTargetPlayerId = ChaseTargetPlayerId,
        Kind = (int)Kind
    };
}
