#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    [MessagePackObject]
    public class G_TO_C_HEART_BEAT : IMessagePackObject
    {
        [Key("utcNow")] public DateTime UtcNow { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_CONNECT : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("matchingId")] public long MatchingId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_CONNECT_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("message")] public string Message { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_MOVE : IMessagePackObject
    {
        [Key("position")] public Vector3f Position { get; set; }
        [Key("velocity")] public Vector3f Velocity { get; set; }
        [Key("rotation")] public float Rotation { get; set; }
        [Key("inputSeq")] public uint InputSequence { get; set; }
        [Key("clientTime")] public long ClientTimestamp { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_MOVE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("position")] public Vector3f Position { get; set; }
        [Key("velocity")] public Vector3f Velocity { get; set; }
        [Key("rotation")] public float Rotation { get; set; }
        [Key("cell")] public Cell Cell { get; set; }
        [Key("lastProcessedInput")] public uint LastProcessedInput { get; set; }
        [Key("serverTime")] public long ServerTimestamp { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_AREA_PLAYER_ENTER : IMessagePackObject
    {
        [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }
        [Key("cell")] public Cell Cell { get; set; } // 최신 Cell 위치
    }

    [MessagePackObject]
    public class G_TO_C_AREA_PLAYER_LEAVE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_AREA_EXIT_BLOCKED : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; } // 나가려던 Area
        [Key("correctedCell")] public Cell CorrectedCell { get; set; } // 되돌아갈 셀 위치
    }

    [MessagePackObject]
    public class G_TO_C_INTERACTABLE_LIST : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("objects")] public List<InteractableObjectState> Objects { get; set; }
        [Key("isEnd")] public bool IsEnd { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_INTERACTABLE_UPDATE : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("order")] public int Order { get; set; }
        [Key("isExplored")] public bool IsExplored { get; set; }
        [Key("exploredBy")] public long ExploredBy { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_SPAWN : IMessagePackObject
    {
        [Key("objectKeyList")] public List<string> ObjectKeyList { get; set; }
        [Key("cellsToRemove")] public List<Cell> CellsToRemove { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_DESTROY : IMessagePackObject
    {
        [Key("objectKey")] public string ObjectKey { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_SPAWN : IMessagePackObject
    {
        [Key("objectKeyList")] public List<string> ObjectKeyList { get; set; }
        [Key("cellsToRemove")] public List<Cell> CellsToRemove { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_DESTROY : IMessagePackObject
    {
        [Key("objectKey")] public string ObjectKey { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_PLAYER_INFO : IMessagePackObject
    {
        [Key("playerInfoList")] public List<PlayerInfo> PlayerInfoList { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_PLAYER_INFO : IMessagePackObject
    {
        [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_ATTACK : IMessagePackObject
    {
        [Key("targetId")] public string TargetId { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class C_TO_G_INTERACT : IMessagePackObject
    {
        [Key("targetId")] public string TargetId { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class G_TO_C_GAME_TIME_WARNING : IMessagePackObject
    {
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("remainingSeconds")] public int RemainingSeconds { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_GAME_END : IMessagePackObject
    {
        [Key("matchingId")] public long MatchingId { get; set; }
        [Key("isEscaped")] public bool IsEscaped { get; set; } // true: 탈출 성공, false: 탈출 실패 (시간 초과)
    }

    [MessagePackObject]
    public class G_TO_C_ERROR : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("message")] public string Message { get; set; }
    }
}
