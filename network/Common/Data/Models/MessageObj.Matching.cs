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
        [Key("gameServerIp")] public string GameServerIp { get; set; }
        [Key("gameServerPort")] public int GameServerPort { get; set; }
        [Key("gameEndTimestamp")] public long GameEndTimestamp { get; set; }
        [Key("playerRoster")] public List<PlayerInfo> PlayerRoster { get; set; } = new();
        [Key("gameHandoffTicket")] public string GameHandoffTicket { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_MATCHING_FAILED : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("matchingId")] public long MatchingId { get; set; }
    }

    /// <summary>
    ///     매치 구성 — user_server가 매치 확정 때 Redis handoff Hash에 한 번 쓰고 Game Server가 첫 접속 때 읽는다.
    ///     "누가 이 매치에 오는가"만 담는다. 스폰·로스터 같은 매치 안의 사실은 Game Server가 정한다.
    /// </summary>
    [MessagePackObject]
    public class MatchManifest
    {
        [Key(0)] public List<long> HumanPlayerIds { get; set; } = new();
        [Key(1)] public List<long> BotPlayerIds { get; set; } = new();
    }
}
