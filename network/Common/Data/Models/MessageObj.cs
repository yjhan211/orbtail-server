#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    public interface IMessagePackObject
    {
    }

    [MessagePackObject]
    public class U_TO_C_HEART_BEAT : IMessagePackObject
    {
        [Key("utcNow")] public DateTime UtcNow { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_HEART_BEAT : IMessagePackObject
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
        [Key("messageTextId")] public int MessageTextId { get; set; } // 표시할 메시지 ID
        [Key("correctedCell")] public Cell CorrectedCell { get; set; } // 되돌아갈 셀 위치
    }

    [MessagePackObject]
    public class G_TO_C_INTERACTABLE_LIST : IMessagePackObject
    {
        [Key("areaType")] public AreaType AreaType { get; set; }
        [Key("objects")] public List<InteractableObjectState> Objects { get; set; }
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
    public class U_TO_C_QUEST_LIST : IMessagePackObject
    {
        [Key("questDict")] public Dictionary<int, QuestInfo> QuestDict { get; set; }
        [Key("isEnd")] public bool IsEnd { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_QUEST_INCREASE : IMessagePackObject
    {
        [Key("questId")] public int QuestId { get; set; }
        [Key("count")] public int Count { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_QUEST_UPDATE : IMessagePackObject
    {
        [Key("quest")] public QuestInfo QuestInfo { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_QUEST_SUCCESS : IMessagePackObject
    {
        [Key("questId")] public int QuestId { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_QUEST_SUCCESS : IMessagePackObject
    {
        [Key("questId")] public int QuestId { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_MAIL_LIST : IMessagePackObject
    {
        [Key("mailDict")] public Dictionary<long, MailInfo> MailDict { get; set; }
        [Key("isEnd")] public bool IsEnd { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_MAIL_RECEIVE : IMessagePackObject
    {
        [Key("mailUid")] public long MailUid { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_MAIL_RECEIVE : IMessagePackObject
    {
        [Key("mailUid")] public long MailUid { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
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
    }

    [MessagePackObject]
    public class U_TO_C_MATCHING_FAILED : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
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
    public class C_TO_G_ATTACK : IMessagePackObject
    {
        [Key("targetId")] public string TargetId { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class C_TO_G_INTERACT : IMessagePackObject
    {
        [Key("targetId")] public string TargetId { get; set; } = string.Empty;
    }

    // 탐색 시작 요청
    [MessagePackObject]
    public class C_TO_G_EXPLORE_START : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
    }

    // 탐색 시작 브로드캐스트 (다른 플레이어에게 애니메이션 동기화)
    [MessagePackObject]
    public class G_TO_C_EXPLORE_START : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("interactId")] public int InteractId { get; set; }
    }

    // 선택지 선택
    [MessagePackObject]
    public class C_TO_G_EXPLORE_SELECT : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("actionId")] public int ActionId { get; set; }
    }

    // 탐색 결과 (요청한 클라이언트에게만)
    [MessagePackObject]
    public class G_TO_C_EXPLORE_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("interactId")] public int InteractId { get; set; }
        [Key("actionId")] public int ActionId { get; set; }
        [Key("itemId")] public int ItemId { get; set; }  // 획득한 아이템 ID (0이면 없음)
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }

    // 탐색 종료 요청 (클라이언트 → 서버)
    [MessagePackObject]
    public class C_TO_G_EXPLORE_END : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
    }

    // 탐색 종료 브로드캐스트 (다른 플레이어에게 애니메이션 종료 동기화)
    [MessagePackObject]
    public class G_TO_C_EXPLORE_END : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
    }

    // 인게임 아이템 정보 (게임 내 배낭용 - 게임 종료 시 초기화)
    [MessagePackObject]
    public class InGameItemInfo
    {
        [Key("itemUid")] public long ItemUid { get; set; }   // MatchingId + Sequence 조합
        [Key("itemId")] public int ItemId { get; set; }     // 아이템 종류
        [Key("count")] public int Count { get; set; }       // 수량
    }

    // 인게임 배낭 전체 목록 (게임 시작 시)
    [MessagePackObject]
    public class G_TO_C_INGAME_INVENTORY_LIST : IMessagePackObject
    {
        [Key("items")] public List<InGameItemInfo> Items { get; set; }
    }

    // 인게임 배낭 업데이트 (아이템 획득/사용 시)
    [MessagePackObject]
    public class G_TO_C_INGAME_INVENTORY_UPDATE : IMessagePackObject
    {
        [Key("items")] public List<InGameItemInfo> Items { get; set; }
    }

    // 인게임 아이템 사용 요청
    [MessagePackObject]
    public class C_TO_G_USE_INGAME_ITEM : IMessagePackObject
    {
        [Key("itemUid")] public long ItemUid { get; set; }
        [Key("count")] public int Count { get; set; }
    }

    // 인게임 아이템 사용 결과
    [MessagePackObject]
    public class G_TO_C_USE_INGAME_ITEM_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("itemUid")] public long ItemUid { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("ruleId")] public int RuleId { get; set; } // 행동 수칙 쪽지 아이템(202000003) 사용 시 규칙 ID
    }

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
    }

    #region 탈출 절차 프로토콜

    // 탈출 절차 단계 정보 응답
    [MessagePackObject]
    public class G_TO_C_EXIT_STEP_INFO : IMessagePackObject
    {
        [Key("groupId")] public int GroupId { get; set; } // 탈출 절차 그룹 ID
        [Key("currentStepOrder")] public int CurrentStepOrder { get; set; } // 현재 단계 (이보다 작은 order는 완료)
        [Key("totalStepCount")] public int TotalStepCount { get; set; } // 총 단계 수
        [Key("isCompleted")] public bool IsCompleted { get; set; } // 탈출 완료 여부
        [Key("lastAdvancedBy")] public long LastAdvancedBy { get; set; } // 마지막으로 진행한 플레이어 UID (0이면 아직 진행 안함)
    }

    // 탈출 절차 다음 단계 진행 요청
    [MessagePackObject]
    public class C_TO_G_EXIT_ADVANCE : IMessagePackObject
    {
        [Key("currentStepOrder")] public int CurrentStepOrder { get; set; } // 검증용 (클라이언트가 생각하는 현재 단계)
    }

    // 탈출 절차 진행 결과
    [MessagePackObject]
    public class G_TO_C_EXIT_ADVANCE_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("escaped")] public bool Escaped { get; set; } // 탈출 완료 여부
        [Key("newStepOrder")] public int NewStepOrder { get; set; }
    }

    // 탈출 절차 단계 변경 브로드캐스트 (다른 플레이어가 진행시켜도 모두에게 알림)
    [MessagePackObject]
    public class G_TO_C_EXIT_STEP_UPDATE : IMessagePackObject
    {
        [Key("advancedByPlayerId")] public long AdvancedByPlayerId { get; set; }
        [Key("newStepOrder")] public int NewStepOrder { get; set; }
        [Key("escaped")] public bool Escaped { get; set; }
    }

    // 로비 복귀 요청
    [MessagePackObject]
    public class C_TO_G_RETURN_TO_LOBBY : IMessagePackObject
    {
    }

    // 로비 복귀 결과
    [MessagePackObject]
    public class G_TO_C_RETURN_TO_LOBBY_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }

    #endregion

    #region Door 프로토콜

    // 문 열기 요청
    [MessagePackObject]
    public class C_TO_G_DOOR_OPEN_REQUEST : IMessagePackObject
    {
        [Key("doorId")] public int DoorId { get; set; }
    }

    // 문 상태 변경 브로드캐스트
    [MessagePackObject]
    public class G_TO_C_DOOR_STATE_UPDATE : IMessagePackObject
    {
        [Key("doorId")] public int DoorId { get; set; }
        [Key("isOpen")] public bool IsOpen { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; } // 실패 시 에러코드
        [Key("openerPlayerId")] public long OpenerPlayerId { get; set; } // 문을 연 플레이어 ID (0이면 없음)
    }

    // 입장 시 열린 문 목록
    [MessagePackObject]
    public class G_TO_C_DOOR_STATE_LIST : IMessagePackObject
    {
        [Key("openDoorIds")] public List<int> OpenDoorIds { get; set; } = new();
    }

    #endregion

    #region 복도 규칙 프로토콜

    /// <summary>
    /// 종소리 이벤트 정보 (게임 시작 기준 상대 시간)
    /// </summary>
    [MessagePackObject]
    public class BellEvent
    {
        [Key("s")] public int StartOffsetSec { get; set; } // 게임 시작 후 시작 시간 (초)
        [Key("d")] public int DurationSec { get; set; } // 지속 시간 (초)
    }

    /// <summary>
    /// 복도 종소리 스케줄 (게임 시작 시 전송)
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_CORRIDOR_BELL : IMessagePackObject
    {
        [Key("bells")] public List<BellEvent> Bells { get; set; } = new();
    }

    #endregion
}
