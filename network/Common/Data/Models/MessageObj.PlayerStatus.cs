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

    // 플레이어 스탯 업데이트 (스태미나/정신력 등)
    [MessagePackObject]
    public class G_TO_C_PLAYER_STATS_UPDATE : IMessagePackObject
    {
        [Key("stamina")] public int Stamina { get; set; }
        [Key("staminaDelta")] public int StaminaDelta { get; set; }
        [Key("corruption")] public int Corruption { get; set; }
        [Key("corruptionDelta")] public int CorruptionDelta { get; set; }
        /// <summary>v0.2.1 — Stamina 부족 시 Cor 변환 발생 여부. true면 클라가 경고 알럿 표시.</summary>
        [Key("staminaConverted")] public bool StaminaConverted { get; set; }
    }
}
