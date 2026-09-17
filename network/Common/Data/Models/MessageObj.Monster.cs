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

}
