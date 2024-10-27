#pragma warning disable CS8618

using MessagePack;
// ReSharper disable All
namespace network.common;

public interface IMessagePackObject
{
}

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

    [Key("jobInfo")] public JobInfo JobInfo { get; set; }

    [Key("labInfo")] public LabInfo LabInfo { get; set; }
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
public class U_TO_C_UPDATE_TUTORIAL : IMessagePackObject
{
    [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }

    [Key("jobInfo")] public JobInfo JobInfo { get; set; }
}

[MessagePackObject]
public class U_TO_C_INVENTORY_ITEM_LIST : IMessagePackObject
{
    [Key("itemDict")] public Dictionary<long, ItemInfo> ItemDict { get; set; }

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
public class G_TO_U_MOVE : IMessagePackObject
{
    [Key("objectInfo")] public GameObjectInfo ObjectInfo { get; set; }
}

[MessagePackObject]
public class U_TO_C_MOVE : IMessagePackObject
{
    [Key("errorCode")] public ErrorCode ErrorCode { get; set; }

    [Key("objectInfo")] public GameObjectInfo ObjectInfo { get; set; }
}

[MessagePackObject]
public class G_TO_U_SPAWN : IMessagePackObject
{
    [Key("objectKeyList")] public List<string> ObjectKeyList { get; set; }
}

[MessagePackObject]
public class G_TO_U_DESTROY : IMessagePackObject
{
    [Key("objectKey")] public string ObjectKey { get; set; }
}

[MessagePackObject]
public class U_TO_C_SPAWN : IMessagePackObject
{
    [Key("objectKeyList")] public List<string> ObjectKeyList { get; set; }

    [Key("isEnd")] public bool IsEnd { get; set; }
}

[MessagePackObject]
public class U_TO_C_DESTROY : IMessagePackObject
{
    [Key("objectKey")] public string ObjectKey { get; set; }
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
public class G_TO_U_PLAYER_INFO : IMessagePackObject
{
    [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }
}

[MessagePackObject]
public class G_TO_U_CAMP_INFO : IMessagePackObject
{
    [Key("campInfo")] public CampInfo CampInfo { get; set; }
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
public class C_TO_U_JOB_RESOURCE_INFO : IMessagePackObject
{
    [Key("jobResourceIdList")] public List<long> JobResourceIdList { get; set; }
}

[MessagePackObject]
public class U_TO_C_JOB_RESOURCE_INFO : IMessagePackObject
{
    [Key("jobResourceInfoList")] public List<JobResourceInfo> JobResourceInfoList { get; set; }
}

[MessagePackObject]
public class G_TO_U_JOB_RESOURCE_INFO : IMessagePackObject
{
    [Key("jobResourceInfo")] public JobResourceInfo JobResourceInfo { get; set; }
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

    [Key("jobInfo")] public JobInfo JobInfo { get; set; }
}

[MessagePackObject]
public class U_TO_C_EXPLORE_COMPLETE : IMessagePackObject
{
    [Key("isSuccess")] public bool IsSuccess { get; set; }

    [Key("jobInfo")] public JobInfo JobInfo { get; set; }
}

[MessagePackObject]
public class C_TO_U_USE_SKILL : IMessagePackObject
{
    [Key("resourceUid")] public long ResourceUid { get; set; }

    [Key("skillId")] public int SkillId { get; set; }
}

[MessagePackObject]
public class U_TO_C_USE_SKILL : IMessagePackObject
{
    [Key("errorCode")] public ErrorCode ErrorCode { get; set; }

    [Key("jobInfo")] public JobInfo JobInfo { get; set; }
}

[MessagePackObject]
public class U_TO_G_USE_SKILL : IMessagePackObject
{
    [Key("resourceUid")] public long ResourceUid { get; set; }
}

[MessagePackObject]
public class U_TO_C_USE_SKILL_COMPLETE : IMessagePackObject
{
    [Key("isSuccess")] public bool IsSuccess { get; set; }

    [Key("itemInfo")] public ItemInfo ItemInfo { get; set; }

    [Key("jobInfo")] public JobInfo JobInfo { get; set; }
}

[MessagePackObject]
public class U_TO_G_LOGOUT : IMessagePackObject
{
    [Key("playerId")] public long PlayerId { get; set; }
}

[MessagePackObject]
public class C_TO_U_GET_JOB : IMessagePackObject
{
    [Key("jobType")] public JobType JobType { get; set; }
}

[MessagePackObject]
public class U_TO_C_GET_JOB : IMessagePackObject
{
    [Key("errorCode")] public ErrorCode ErrorCode { get; set; }

    [Key("jobInfo")] public JobInfo JobInfo { get; set; }
}

[MessagePackObject]
public class C_TO_U_UPGRADE_JOB : IMessagePackObject
{
    [Key("jobType")] public JobType JobType { get; set; }
}

[MessagePackObject]
public class U_TO_C_UPGRADE_JOB : IMessagePackObject
{
    [Key("errorCode")] public ErrorCode ErrorCode { get; set; }

    [Key("jobInfo")] public JobInfo JobInfo { get; set; }
}

[MessagePackObject]
public class C_TO_U_WEAR_ITEM : IMessagePackObject
{
    [Key("itemUid")] public long ItemUid { get; set; }
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
}

[MessagePackObject]
public class U_TO_C_USE_ITEM : IMessagePackObject
{
    [Key("jobInfo")] public JobInfo JobInfo { get; set; }
}

[MessagePackObject]
public class U_TO_C_CHANGE_MAP : IMessagePackObject
{
    [Key("mapId")] public MapId MapId { get; set; }

    [Key("mapSubId")] public long MapSubId { get; set; }

    [Key("spawnCell")] public Cell SpawnCell { get; set; }

    [Key("isFlip")] public bool IsFlip { get; set; }
}

[MessagePackObject]
public class C_TO_U_CREATE_LAB : IMessagePackObject
{
    [Key("labName")] public string LabName { get; set; }
}

[MessagePackObject]
public class U_TO_C_CREATE_LAB : IMessagePackObject
{
    [Key("playerInfo")] public PlayerInfo PlayerInfo { get; set; }

    [Key("labInfo")] public LabInfo LabInfo { get; set; }
}

[MessagePackObject]
public class G_TO_U_CREATE_INSTANCE_SUCCESS : IMessagePackObject
{
    [Key("mapId")] public MapId MapId { get; set; }

    [Key("mapSubId")] public long MapSubId { get; set; }
}

[MessagePackObject]
public class C_TO_U_UPGRADE_RESEARCH : IMessagePackObject
{
    [Key("jobType")] public JobType JobType { get; set; }

    [Key("researchId")] public int ResearchId { get; set; }
}

[MessagePackObject]
public class U_TO_C_UPGRADE_RESEARCH : IMessagePackObject
{
    [Key("researchInfoDict")] public Dictionary<int, ResearchInfo> ResearchInfoDict { get; set; }

    [Key("jobInfo")] public JobInfo JobInfo { get; set; }
}

[MessagePackObject]
public class C_TO_U_MAKE : IMessagePackObject
{
    [Key("materials")] public Dictionary<long, int> Materials { get; set; }
}

[MessagePackObject]
public class U_TO_C_MAKE : IMessagePackObject
{
    [Key("isSuccess")] public bool IsSuccess { get; set; }
}

[MessagePackObject]
public class C_TO_U_WRITE_LAB_HIRE : IMessagePackObject
{
    [Key("comment")] public string Comment { get; set; }
}

[MessagePackObject]
public class U_TO_C_WRITE_LAB_HIRE : IMessagePackObject
{
    [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
}

[MessagePackObject]
public class U_TO_C_LAB_HIRE_LIST : IMessagePackObject
{
    [Key("hireList")] public List<(long, string, string)> HireList { get; set; }
}

[MessagePackObject]
public class C_TO_U_JOIN_LAB : IMessagePackObject
{
    [Key("labId")] public long LabId { get; set; }
}

[MessagePackObject]
public class U_TO_C_LAB_INFO : IMessagePackObject
{
    [Key("joinPlayerInfo")] public PlayerInfo JoinPlayerInfo { get; set; }

    [Key("labInfo")] public LabInfo LabInfo { get; set; }
}

[MessagePackObject]
public class C_TO_U_LAB_INVENTORY_ADD_ITEM : IMessagePackObject
{
    [Key("itemUid")] public long ItemUid { get; set; }
}

[MessagePackObject]
public class C_TO_U_LAB_INVENTORY_TAKE_ITEM : IMessagePackObject
{
    [Key("itemUid")] public long ItemUid { get; set; }
}

[MessagePackObject]
public class U_TO_U_LAB_INVENTORY : IMessagePackObject
{
    [Key("itemDict")] public Dictionary<long, ItemInfo> ItemDict { get; set; }
}

[MessagePackObject]
public class U_TO_C_LAB_INVENTORY : IMessagePackObject
{
    [Key("itemDict")] public Dictionary<long, ItemInfo> ItemDict { get; set; }

    [Key("isEnd")] public bool IsEnd { get; set; }
}

[MessagePackObject]
public class C_TO_U_ENCAMP : IMessagePackObject
{
    [Key("itemUid")] public long ItemUid { get; set; }
}

[MessagePackObject]
public class U_TO_C_CAMP_INFO : IMessagePackObject
{
    [Key("campInfoList")] public List<CampInfo> CampInfoList { get; set; }
}

[MessagePackObject]
public class C_TO_U_CAMP_INFO : IMessagePackObject
{
    [Key("campInfoList")] public List<long> CampInfoList { get; set; }
}

[MessagePackObject]
public class C_TO_U_ADD_SELL_ITEM : IMessagePackObject
{
    [Key("itemUid")] public long ItemUid { get; set; }

    [Key("price")] public int Price { get; set; }
}

[MessagePackObject]
public class C_TO_U_DELETE_SELL_ITEM : IMessagePackObject
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