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

    [MessagePackObject]
    public class MatchManifest
    {
        [Key(0)] public List<long> HumanPlayerIds { get; set; } = new();
        [Key(1)] public List<long> BotPlayerIds { get; set; } = new();
        [Key(2)] public MatchMode Mode { get; set; }
    }

    public enum MatchMode
    {
        Normal = 0,
        SoloMapValidation = 1
    }
}
