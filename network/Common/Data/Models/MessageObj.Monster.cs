#pragma warning disable CS8618
using System.Collections.Generic;
using MessagePack;
using network.common;

namespace network.common.data.models
{

    [MessagePackObject]
    public sealed class G_TO_C_MONSTER_SNAPSHOT : IMessagePackObject
    {
        [Key("monsters")] public List<MonsterInfo> Monsters { get; set; } = new();
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
