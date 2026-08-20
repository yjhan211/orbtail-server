#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    [MessagePackObject]
    public class U_TO_C_HEART_BEAT : IMessagePackObject
    {
        [Key("utcNow")] public DateTime UtcNow { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_LOGIN : IMessagePackObject
    {
        [Key("accountToken")] public string AccountToken { get; set; } // TODO 계정키로 변경
    }

    [MessagePackObject]
    public class U_TO_C_LOGIN : IMessagePackObject
    {
        [Key("objectInfo")] public GameObjectInfo ObjectInfo { get; set; }
        [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_INVENTORY_ITEM_LIST : IMessagePackObject
    {
        [Key("itemDict")] public Dictionary<long, ItemInfo> ItemDict { get; set; }

        [Key("isEnd")] public bool IsEnd { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_INVENTORY_UPDATE : IMessagePackObject
    {
        [Key("updateItemDict")] public List<ItemInfo> UpdateItems { get; set; }
        [Key("isEnd")] public bool IsEnd { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_EXPLORE : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_WEAR_ITEM : IMessagePackObject
    {
        [Key("itemUidList")] public List<long> ItemUidList { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_WEAR_ITEM : IMessagePackObject
    {
        [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_USE_ITEM : IMessagePackObject
    {
        [Key("itemUid")] public long ItemUid { get; set; }
        [Key("targetItemUid")] public long TargetItemUid { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_USE_ITEM : IMessagePackObject
    {
        [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_CHANGE_MAP : IMessagePackObject
    {
        [Key("mapId")] public MapId MapId { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_SOCIAL_ACTION : IMessagePackObject
    {
        [Key("socialType")] public SocialActionType SocialActionType { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_SOCIAL_ACTION : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("socialType")] public SocialActionType SocialActionType { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_ERROR : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("message")] public string Message { get; set; }
    }
}
