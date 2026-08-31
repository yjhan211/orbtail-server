#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    [MessagePackObject]
    public class C_TO_U_MATCHING : IMessagePackObject
    {
    }

    [MessagePackObject]
    public class U_TO_C_MATCHING : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_MATCHING_CANCEL : IMessagePackObject
    {
    }

    [MessagePackObject]
    public class U_TO_C_MATCHING_CANCEL : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_MATCHING_SUCCESS : IMessagePackObject
    {
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("mapId")] public MapId MapId { get; set; }
        [Key("mapSubId")] public long MapSubId { get; set; }
        [Key("spawnPosition")] public Cell SpawnPosition { get; set; }
        [Key("gameServerIp")] public string GameServerIp { get; set; }
        [Key("gameServerPort")] public int GameServerPort { get; set; }
        [Key("gameEndTimestamp")] public long GameEndTimestamp { get; set; }

        // 미니맵 타깃 마커 대상
        [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
        [Key("playerRoster")] public List<PlayerInfo> PlayerRoster { get; set; } = new();
        [Key("activeBuffIds")] public List<int> ActiveBuffIds { get; set; } = new();
        [Key("gameHandoffTicket")] public string GameHandoffTicket { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_MATCHING_FAILED : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("matchingId")] public long MatchingId { get; set; }
    }

    /// <summary>
    ///     봇 채움 매칭 때 Redis에 저장되는 봇 정보
    /// </summary>
    [MessagePackObject]
    public class BotMatchingInfo
    {
        [Key(0)] public long PlayerId { get; set; }
        [Key(1)] public long TargetPlayerId { get; set; }
        [Key(4)] public PersonaType Persona { get; set; }
        [Key(5)] public AreaType StartArea { get; set; }
        [Key(6)] public List<int> ActiveBuffIds { get; set; } = new();
        [Key(7)] public Cell SpawnCell { get; set; } = new(0, 0);
    }
}
