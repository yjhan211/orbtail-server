#pragma warning disable CS8618
using System.Collections.Generic;
using MessagePack;
using network.common;

namespace network.common.data.models
{
    /// <summary>
    /// Server-authoritative state for a fixed emotion-afterimage monster.
    /// The client uses this only to drive the scene prefab and never predicts HP.
    /// </summary>
    [MessagePackObject]
    public sealed class MonsterRuntimeInfo
    {
        [Key("monsterId")] public int MonsterId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("positionX")] public float PositionX { get; set; }
        [Key("positionY")] public float PositionY { get; set; }
        [Key("maxHealth")] public int MaxHealth { get; set; }
        [Key("currentHealth")] public int CurrentHealth { get; set; }
        [Key("isAlive")] public bool IsAlive { get; set; }
        [Key("rewardItemId")] public int RewardItemId { get; set; }
        [Key("isCore")] public bool IsCore { get; set; }
        [Key("summonStoneReward")] public int SummonStoneReward { get; set; }
        [Key("chaseTargetPlayerId")] public long ChaseTargetPlayerId { get; set; }
    }

    [MessagePackObject]
    public sealed class G_TO_C_MONSTER_SNAPSHOT : IMessagePackObject
    {
        [Key("monsters")] public List<MonsterRuntimeInfo> Monsters { get; set; } = new();
    }

    /// <summary>Visual-only confirmation that a monster's authoritative contact attack landed.</summary>
    [MessagePackObject]
    public sealed class G_TO_C_MONSTER_ATTACK_VFX : IMessagePackObject
    {
        [Key("monsterId")] public int MonsterId { get; set; }
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("areaType")] public AreaType AreaType { get; set; }
    }
}
