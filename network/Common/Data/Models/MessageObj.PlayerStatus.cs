#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    // 플레이어 상태 변경 요청
    [MessagePackObject]
    public class C_TO_G_PLAYER_STATE : IMessagePackObject
    {
        [Key("state")] public PlayerState State { get; set; }
    }

    // 플레이어 상태 브로드캐스트
    [MessagePackObject]
    public class G_TO_C_PLAYER_STATE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("state")] public PlayerState State { get; set; }
    }

    // 플레이어 체력 업데이트. 델타는 피해 시 음수, 회복 시 양수.
    [MessagePackObject]
    public class G_TO_C_PLAYER_STATS_UPDATE : IMessagePackObject
    {
        [Key("health")] public int Health { get; set; }
        [Key("healthDelta")] public int HealthDelta { get; set; }
    }
}
