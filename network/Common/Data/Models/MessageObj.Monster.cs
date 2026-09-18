#pragma warning disable CS8618
using System.Collections.Generic;
using MessagePack;
using network.common;

namespace network.common.data.models
{

    [MessagePackObject]
    public sealed class G_TO_C_MONSTER_INFO : IMessagePackObject
    {
        [Key("monsters")] public List<MonsterInfo> Monsters { get; set; } = new();
    }

    [MessagePackObject]
    public sealed class MonsterDeathInfo
    {
        [Key("monsterId")] public int MonsterId { get; set; }
        [Key("killerPlayerId")] public long KillerPlayerId { get; set; }
    }

    [MessagePackObject]
    public sealed class G_TO_C_MONSTER_DEATH : IMessagePackObject
    {
        [Key("deaths")] public List<MonsterDeathInfo> Deaths { get; set; } = new();
    }
}
