using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>몬스터 한 개체의 상태와 피해 적용을 담당한다. 매치 잠금 안에서 사용한다.</summary>
public sealed class Monster
{
    private readonly MonsterInsignia _insignia;
    private readonly float _attackRange;
    public static float BaseContactRadius => Config.SWARM_MONSTER_BASE_CONTACT_RADIUS;
    public static float GetContactRadius(MonsterKind kind) => BaseContactRadius * (SwarmMonsterData.Get((int)kind)?.ContactRadiusScale ?? 1f);

    public MonsterInfo Info { get; } = new()
    {
        ObjectInfo = new GameObjectInfo { ObjectType = ObjectType.MONSTER, MapId = Config.SWARM_MATCH_MAP },
        RewardItemId = OrbData.GetTierOneItemId(OrbGroupIds.Sun)
    };
    public int MonsterId { get => Info.MonsterId; init => Info.MonsterId = value; }
    public MonsterKind Kind { get => (MonsterKind)Info.Kind; init => Info.Kind = (int)value; }
    public MonsterInsignia Insignia
    {
        get => _insignia;
        init
        {
            _insignia = value;
            Info.RewardItemId = value switch
            {
                MonsterInsignia.Sun => OrbData.GetTierOneItemId(OrbGroupIds.Sun),
                MonsterInsignia.Wind => OrbData.GetTierOneItemId(OrbGroupIds.Wind),
                _ => OrbData.GetTierOneItemId(OrbGroupIds.Wave)
            };
        }
    }
    public int PhaseTier { get => Info.Phase; set => Info.Phase = value; }
    public int MaxHealthValue { get => Info.MaxHealth; init => Info.MaxHealth = value; }
    public int ContactDamageValue { get; init; }
    public float AttackRangeValue { get => _attackRange > 0f ? _attackRange : GetContactRadius(Kind); init => _attackRange = value; }
    public float AttackCooldownValue { get; init; }
    public int SummonStoneReward { get => Info.SummonStoneReward; init => Info.SummonStoneReward = value; }
    public int HeartReward { get; init; }
    public Vector3f Position
    {
        get => Info.ObjectInfo.Position;
        set
        {
            Info.ObjectInfo.Position = value;
            Info.ObjectInfo.Cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, value);
        }
    }
    public MovementState Movement { get; } = new();
    public int Health { get => Info.CurrentHealth; set => Info.CurrentHealth = value; }
    public bool Alive { get => Info.IsAlive; set => Info.IsAlive = value; }
    public long ChaseTargetPlayerId { get => Info.ChaseTargetPlayerId; set => Info.ChaseTargetPlayerId = value; }
    public DateTime NextContactAtUtc { get; set; }
    public DateTime DiedAtUtc { get; set; }

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

    public MonsterInfo ToMonsterInfo() => new()
    {
        ObjectInfo = Info.ObjectInfo.Clone(),
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
