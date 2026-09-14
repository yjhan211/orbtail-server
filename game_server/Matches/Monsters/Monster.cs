using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>몬스터 한 개체의 상태와 피해·감속·공격 예약 변경을 담당한다. 매치 잠금 안에서 사용한다.</summary>
public sealed class Monster
{
    private readonly MonsterInsignia _insignia;

    public static float BaseContactRadius => Config.SWARM_MONSTER_BASE_CONTACT_RADIUS;

    public static float GetContactRadius(MonsterKind kind) => BaseContactRadius * (SwarmMonsterData.Get((int)kind)?.ContactRadiusScale ?? 1f);

    // 공통 값은 Info에만 보관하고, 전송할 때는 별도 스냅샷으로 복사한다.
    public MonsterInfo Info { get; } = new() { RewardItemId = 107000010 };

    public int MonsterId { get => Info.MonsterId; init => Info.MonsterId = value; }
    public long CombatTargetId { get; init; }
    public MonsterInsignia Insignia
    {
        get => _insignia;
        init
        {
            _insignia = value;
            Info.RewardItemId = value switch
            {
                MonsterInsignia.Sun => 107000010,
                MonsterInsignia.Wind => 107000020,
                _ => 107000030
            };
        }
    }
    public AreaType Area { get => Info.AreaType; set => Info.AreaType = value; }
    public Vector3f Position
    {
        get => Info.ObjectInfo.Position;
        set
        {
            Info.ObjectInfo.Position = value;
            Info.ObjectInfo.Cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, value);
        }
    }
    public int Health { get => Info.CurrentHealth; set => Info.CurrentHealth = value; }
    public bool Alive { get => Info.IsAlive; set => Info.IsAlive = value; }
    public DateTime ActivatesAtUtc { get; set; }
    public DateTime NextContactAtUtc { get; set; }
    public DateTime DiedAtUtc { get; set; }
    public DateTime SpawnedAtUtc { get; set; }
    public float ScatterAngle { get; init; }
    public DateTime WaveSlowUntilUtc { get; set; }
    public int SummonStoneReward { get => Info.SummonStoneReward; init => Info.SummonStoneReward = value; }
    public int HeartReward { get; init; }
    public int ContactDamageValue { get; init; }
    public long ChaseTargetPlayerId { get => Info.ChaseTargetPlayerId; set => Info.ChaseTargetPlayerId = value; }
    public MonsterKind Kind { get => (MonsterKind)Info.Kind; init => Info.Kind = (int)value; }
    public int MaxHealthValue { get => Info.MaxHealth; init => Info.MaxHealth = value; }
    public float AttackRangeValue { get; init; }
    public float AttackCooldownValue { get; init; }
    public DateTime NextTargetScanAtUtc { get; set; }
    public float AnchorX { get; set; }
    public float AnchorY { get; set; }
    public bool Aggro { get; set; }
    public AreaType HomeArea { get; set; }
    public int PhaseTier { get => Info.Phase; set => Info.Phase = value; }
    public long OwnerPlayerId { get; set; }
    public int PendingDamage { get; set; }
    public MovementState Movement { get; } = new();

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

    public MonsterInfo ToMonsterInfo() => new()
    {
        MonsterId = MonsterId,
        AreaType = Area,
        PositionX = Position.X,
        PositionY = Position.Y,
        MaxHealth = MaxHealthValue,
        CurrentHealth = Health,
        IsAlive = Alive,
        RewardItemId = Info.RewardItemId,
        IsCore = Info.IsCore,
        Phase = PhaseTier,
        SummonStoneReward = SummonStoneReward,
        ChaseTargetPlayerId = ChaseTargetPlayerId,
        Kind = (int)Kind
    };
}
