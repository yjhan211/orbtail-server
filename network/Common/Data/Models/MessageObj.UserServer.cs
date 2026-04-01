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
    public class C_TO_U_SET_NAME : IMessagePackObject
    {
        [Key("name")] public string Name { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_SET_NAME : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }

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
    public class C_TO_U_CHAT_MSG : IMessagePackObject
    {
        [Key("chatType")] public ChatType ChatType { get; set; }

        [Key("chatMessage")] public string ChatMessage { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_CHAT_MSG : IMessagePackObject
    {
        [Key("chatType")] public ChatType ChatType { get; set; }
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("name")] public string Name { get; set; }
        [Key("chatMessage")] public string ChatMessage { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_MOVE : IMessagePackObject
    {
        [Key("direction")] public DirectionType Direction { get; set; }
    }

    [MessagePackObject]
    public class U_TO_G_MOVE : IMessagePackObject
    {
        [Key("objectInfo")] public GameObjectInfo ObjectInfo { get; set; }

        [Key("targetCell")] public Cell TargetCell { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_UPDATE_OBJECT : IMessagePackObject
    {
        [Key("objectInfo")] public GameObjectInfo ObjectInfo { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_MAP_UPDATE : IMessagePackObject
    {
        [Key("objectList")] public List<GameObjectInfo> ObjectList { get; set; }

        [Key("serverTimestamp")] public DateTime ServerTimestamp { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_PLAYER_INFO : IMessagePackObject
    {
        [Key("playerIdList")] public List<long> PlayerIdList { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_PLAYER_INFO : IMessagePackObject
    {
        [Key("playerInfoList")] public List<PlayerInfo> PlayerInfoList { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_OBJECT_INFO : IMessagePackObject
    {
        [Key("objectKeyList")] public List<string> ObjectKeyList { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_OBJECT_INFO : IMessagePackObject
    {
        [Key("objectInfoList")] public List<GameObjectInfo> ObjectInfoList { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_EXPLORE_TARGET_INFO : IMessagePackObject
    {
        [Key("exploreTargetIdList")] public List<long> ExploreTargetIdList { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_EXPLORE_TARGET_INFO : IMessagePackObject
    {
        [Key("exploreTargetList")] public List<ExploreTargetInfo> ExploreTargetList { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_EXPLORE_TARGET_INFO : IMessagePackObject
    {
        [Key("exploreTargetInfo")] public ExploreTargetInfo ExploreTargetInfo { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_EXPLORE : IMessagePackObject
    {
        [Key("exploreTargetUid")] public long ExploreTargetUid { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_EXPLORE : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_EXPLORE_COMPLETE : IMessagePackObject
    {
        [Key("isSuccess")] public bool IsSuccess { get; set; }
        [Key("itemId")] public int ItemId { get; set; }
    }

    [MessagePackObject]
    public class U_TO_G_LOGOUT : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
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
    public class U_TO_C_CHANGE_MAP_SUCCESS : IMessagePackObject
    {
        [Key("lastMapId")] public MapId LastMapId { get; set; }
        [Key("mapId")] public MapId MapId { get; set; }

        [Key("mapSubId")] public long MapSubId { get; set; }

        [Key("spawnCell")] public Cell SpawnCell { get; set; }

        [Key("isFlip")] public bool IsFlip { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_ENTER_INSTANCE_SUCCESS : IMessagePackObject
    {
        [Key("mapId")] public MapId MapId { get; set; }

        [Key("mapSubId")] public long MapSubId { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_ENCAMP : IMessagePackObject
    {
        [Key("itemUid")] public long ItemUid { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_BUY_ITEM : IMessagePackObject
    {
        [Key("sellerId")] public long SellerId { get; set; }

        [Key("sellItemUid")] public long SellItemUid { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_BUY_ITEM : IMessagePackObject
    {
        [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_UPDATE_HP : IMessagePackObject
    {
        [Key("addHp")] public int AddHp { get; set; }

        [Key("currentHp")] public int CurrentHp { get; set; }
    }

    [MessagePackObject]
    public class U_TO_U_PLAYER_INFO : IMessagePackObject
    {
        [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_SOCIAL_ACTION : IMessagePackObject
    {
        [Key("socialType")] public SocialActionType SocialActionType { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_SOCIAL_ACTION : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("socialType")] public SocialActionType SocialActionType { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_SOCIAL_ACTION : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("socialType")] public SocialActionType SocialActionType { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_CHANGE_MAP : IMessagePackObject
    {
        [Key("mapId")] public MapId MapId { get; set; }
        [Key("mapSubId")] public long MapSubId { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_ITEM_PUT : IMessagePackObject
    {
        [Key("itemUid")] public long ItemUid { get; set; }
        [Key("cell")] public Cell Cell { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_ITEM_PUT : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_TAKE_DAMAGE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("damageType")] public DamageType DamageType { get; set; }
        [Key("damage")] public int Damage { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_TAKE_DAMAGE : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("damageType")] public DamageType DamageType { get; set; }
        [Key("damage")] public int Damage { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_ENVIRONMENT : IMessagePackObject
    {
        [Key("damageType")] public DamageType DamageType { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_ERROR : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("message")] public string Message { get; set; }
    }
}
