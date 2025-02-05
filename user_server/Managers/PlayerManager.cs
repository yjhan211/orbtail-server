using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.infrastructure;
using network.managers;
using network.packets;
using user_server.controllers;
using user_server.handlers;

namespace user_server.managers;

public delegate void SendPacketDelegate(Packet msg);
public delegate float GetMoveSpeedDelegate();
public delegate void IncreaseHpDelegate(int value);

public class PlayerManager(
    LogManager? logManager,
    NatsClient natsClient,
    SendPacketDelegate sendToClient,
    UpdateObjectManager updateObjectManager)
{
    // ReSharper disable once UnusedMember.Local
    private readonly LogManager? _logManager = logManager;
    private EnvironmentHandler? _environmentHandler;
    private MovementHandler? _movementHandler;
    
    public PlayerInfo? PlayerInfo { get; private set; }
    public GameObjectInfo? ObjectInfo => PlayerInfo?.ObjectInfo ?? null;
    public long PlayerId => PlayerInfo?.PlayerId ?? 0;
    public MapId MapId => PlayerInfo?.ObjectInfo.MapId ?? MapId.None;
    public long MapSubId => PlayerInfo?.ObjectInfo.MapSubId ?? 0;
    public Cell? CurrentCell => PlayerInfo?.ObjectInfo.CurrentCell ?? null;
    public bool IsFlip => PlayerInfo?.ObjectInfo.IsFlip ?? false;
    public PlayerState State => PlayerInfo?.State ?? PlayerState.NONE;
    public string ObjectKey => PlayerInfo?.ObjectInfo.GetGameObjectKey() ?? string.Empty;

    [MemberNotNull(nameof(PlayerInfo))]
    public void Initialize(PlayerInfo playerInfo, EnvironmentHandler environmentHandler)
    {
        PlayerInfo = playerInfo;
        PlayerInfo.ObjectInfo.CurrentCell = PlayerInfo.ObjectInfo.TargetCell;
        PlayerInfo.State = PlayerState.IDLE;
        
        _movementHandler = new MovementHandler(_logManager, PlayerInfo.ObjectInfo, natsClient, sendToClient, updateObjectManager, GetMoveSpeed, IncreaseHp);
        _environmentHandler = environmentHandler;
    }

    public async Task ChangeMap(bool isLogin = false)
    {
        if (ObjectInfo == null || PlayerInfo == null)
        {
            return;
        }
        
        var changeMapInfo = GameMapData.GetPortalOrNull(ObjectInfo, PlayerInfo.IsTutorial);
        if (changeMapInfo == null)
        {
            return;
        }
        
        // 기존 맵에 삭제 요청
        await PublishDestroy();
        await EnterMap(changeMapInfo.Value.mapId, changeMapInfo.Value.spawnPosition, changeMapInfo.Value.isFlip, isLogin);
    }

    public async Task EnterMap(MapId mapId, Cell spawnPosition, bool isFlip, bool isLogin)
    {
        if (PlayerInfo == null)
        {
            return;
        }
        
        PlayerInfo.ObjectInfo.MapId = mapId;
        PlayerInfo.ObjectInfo.MapSubId = GameMapData.IsCommonMap(mapId) ? 0 : GetInstanceMapSubId();
        PlayerInfo.ObjectInfo.CurrentCell = spawnPosition;
        PlayerInfo.ObjectInfo.TargetCell = spawnPosition;
        PlayerInfo.ObjectInfo.IsFlip = isFlip;
        await PlayerInfo.ObjectInfo.Save();

        if (GameMapData.IsCommonMap(MapId) && !isLogin)
        {
            using var packet = PacketMaker.U_TO_C_CHANGE_MAP(MapId, MapSubId, PlayerInfo.ObjectInfo.CurrentCell, IsFlip);
            sendToClient(packet);
            return;
        }

        var serverId = MapHelper.GetManageServerId(MapSubId);
        var subject = SubjectHelper.GetEnterInstanceSubject(serverId);
        var publishObj = MessagePackSerializer.Serialize((PlayerInfo.ObjectInfo.GetGameObjectKey(), MapId, MapSubId, isLogin));
        _logManager?.WriteDebugLog($"EnterMap subject: {subject} {MapId} {MapSubId}");
        natsClient.Publish(subject, publishObj);
    }

    private long GetInstanceMapSubId()
    {
        if (ObjectInfo == null)
        {
            throw new Exception("ObjectInfo is null");
        }
        
        // TODO 동아리
        return ObjectInfo.ObjectId;
    }

    public async Task Spawn()
    {
        if (_movementHandler == null) return;

        await _movementHandler.Spawn();
    }

    public async Task RequestMove(GameUser user, C_TO_U_MOVE body)
    {
        if (_movementHandler == null) return;
        if (PlayerInfo == null) return;

        switch (PlayerInfo.State)
        {
            case PlayerState.SITGROUND:
                PlayerInfo.State = PlayerState.IDLE;
                await PlayerInfo.Save();
                user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
                break;
        }

        if (PlayerInfo.Boosts.Contains(BoostType.SPEED))
        {
            if (PlayerInfo.Hp < 5)
            {
                PlayerInfo.Boosts.Remove(BoostType.SPEED);
                await PlayerInfo.Save();
                user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
            }
        }

        await _movementHandler.ProcessAsync(body);
    }

    public async Task UpdateBoost(GameUser user, C_TO_U_BOOST body)
    {
        if (PlayerInfo == null) return;
        
        if (PlayerInfo.Boosts.TryGetValue(body.BoostType, out var boost))
        {
            PlayerInfo.Boosts.Remove(boost);
        }
        else
        {
            PlayerInfo.Boosts.Add(body.BoostType);
        }

        await PlayerInfo.Save();
        user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
    }

    public async Task SocialAction(GameUser user, C_TO_U_SOCIAL_ACTION body)
    {
        if (PlayerInfo == null) return;
        
        switch (body.SocialActionType)
        {
            case SocialActionType.SITGROUND:
                PlayerInfo.State = PlayerInfo.State == PlayerState.IDLE ? PlayerState.SITGROUND : PlayerState.IDLE;
                await PlayerInfo.Save();
                user.BroadcastUpdateInfo(PlayerInfo);
                break;

            default:
                user.BroadcastSocialAction(PlayerInfo, body.SocialActionType);
                break;
        }
    }

    public async Task SetName(GameUser user, C_TO_U_SET_NAME body)
    {
        if (PlayerInfo == null) return;
        
        PlayerInfo.Name = body.Name;
        await PlayerInfo.Save();
        
        using var packet = PacketMaker.U_TO_C_SET_NAME(ErrorCode.SUCCESS, PlayerInfo);
        user.Send(packet);
        user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
    }

    public async Task<List<ItemInfo>> Wear(long itemUid)
    {
        if (PlayerInfo == null)
        {
            return [];
        }
        
        if (!PlayerInfo.InventoryInfo.ItemDict.TryGetValue(itemUid, out var targetItem))
            throw new Exception($"Item with uid {itemUid} not found");

        var itemDetail = GameItemData.Get(targetItem.ItemId);
        if (!itemDetail.IsEquipment)
            throw new Exception($"not wearable item {targetItem.ItemId}");
        
        // if (!GameDataHelper.IsWearableJobInfo(targetItem.ItemId, JobInfo.JobStatDict))
        //     throw new Exception($"not wearable job type. {targetItem.ItemId}");
        
        var updateItemList = new List<ItemInfo>() { targetItem };
        if (targetItem.IsWear)
        {
            // 착용 해제
            PlayerInfo.WearItemIdList.Remove(targetItem.ItemId);
            targetItem.IsWear = false;
            return updateItemList;
        }

        // 같은 종류의 아이템 인덱스 찾기
        var lastWearItem = PlayerInfo.InventoryInfo.ItemDict.Values.FirstOrDefault(
            item => GameItemData.GetEquipType(targetItem.ItemId) == GameItemData.GetEquipType(item.ItemId) && item.IsWear
        );
        
        if (lastWearItem != null)
        {
            // 같은 종류 아이템 착용 해제
            lastWearItem.IsWear = false;
            PlayerInfo.WearItemIdList.Remove(lastWearItem.ItemId);
            updateItemList.Add(lastWearItem);
        }
        
        // 새로운 아이템 착용
        targetItem.IsWear = true;
        PlayerInfo.WearItemIdList.Add(targetItem.ItemId);
        
        await PlayerInfo.Save();

        return updateItemList;
    }

    public async Task<List<ItemInfo>> UseItem(GameUser user, long itemUid, int count = 1)
    {
        if (PlayerInfo == null)
        {
            return [];
        }

        if (!PlayerInfo.InventoryInfo.ItemDict.TryGetValue(itemUid, out var targetItem))
        {
            throw new Exception($"Item with uid {itemUid} not found");
        }

        if (targetItem.Count < count)
        {
            throw new Exception($"Item with uid {itemUid} count not enough");
        }

        var itemDetail = GameItemData.Get(targetItem.ItemId);
        if (!itemDetail.IsConsumable)
            throw new Exception($"not consumable item {targetItem.ItemId}");

        var updateItemList = new List<ItemInfo>();
        var deleteItem = PlayerInfo.InventoryInfo.DeleteItem(itemUid, count);
        if (deleteItem == null)
        {
            throw new Exception($"delete item {itemUid} failed.");
        }
        updateItemList.Add(deleteItem);
        
        foreach (var (buffId, value) in itemDetail.ConsumableBuffList)
        {
            var buffDetail = GameBuffData.Get(buffId);
            if (buffDetail.Type != BuffType.INSTANT)
            {
                throw new NotImplementedException();
            }
            switch (buffDetail.SubType)
            {
                case BuffSubType.CONDITION_ADD:
                    PlayerInfo.Hp = Math.Clamp(PlayerInfo.Hp + (value * 100), 0, 10000);
                    break;
                
                case BuffSubType.CRAFT_ADD:
                    PlayerInfo.CraftInfo.Manuals.Add(value);
                    break;
            }
        }
        await PlayerInfo.Save();

        switch (targetItem.ItemId)
        {
            case 202000001:
                await QuestController.IncreaseQuestCount(user, 200000001, 1);
                break;
            
            default: 
                await QuestController.IncreaseQuestCount(user, 100000005, 1);
                break;
        }
        
        return updateItemList;
    }

    public async Task SetState(PlayerState newState)
    {
        if (PlayerInfo == null) return;
        
        PlayerInfo.State = newState;
        await PlayerInfo.Save();
    }

    public async Task SetFlip(DirectionType direction)
    {
        if (PlayerInfo == null) return;
        if (direction == DirectionType.NONE)
        {
            return;
        }

        PlayerInfo.ObjectInfo.SetFlip(direction);
        await PlayerInfo.ObjectInfo.Save();

        var isCommonMap = GameMapData.IsCommonMap(MapId);
        var managePartKey = isCommonMap ? MapHelper.CreatePartKey(MapId, PlayerInfo.ObjectInfo.CurrentCell) : MapHelper.CreatePartKey(MapId, MapSubId);
        var manageServer = isCommonMap ? MapHelper.GetManageServerId(managePartKey) : MapHelper.GetManageServerId(MapSubId);
        var subject = SubjectHelper.GetUpdateManageSubject(PlayerInfo.ObjectInfo, manageServer);

        natsClient.Publish(subject, MessagePackSerializer.Serialize((mamagePartKey: managePartKey, ObjectInfo)));
        updateObjectManager.EnqueueUpdateObject(PlayerInfo.ObjectInfo);
    }

    private float GetMoveSpeed()
    {
        if (PlayerInfo == null) return 0;
        
        if (PlayerInfo.Boosts.Contains(BoostType.SPEED))
        {
            return 2;
        }

        if (PlayerInfo.Hp <= 0)
        {
            return 0.5f;
        }

        return 1;
    }

    private void IncreaseHp(int value)
    {
        if (PlayerInfo == null) return;
        
        PlayerInfo.Hp = Math.Clamp(PlayerInfo.Hp + value, 0, 10000);
        
        // 너무 빈번해서 레디스에는 업데이트 안하고 있음
        // TODO 레이드 시 파티 단위로 브로드캐스트
        using var packet = PacketMaker.U_TO_C_PLAYER_INFO([PlayerInfo]);
        sendToClient(packet);
    }

    public async Task Dispose()
    {
        await PublishDestroy();

        _movementHandler?.Dispose();
        _environmentHandler?.Dispose();
    }

    // 접속 종료 시 자신의 object_info 삭제 요청 (PublishLeave랑 다른 점 - 후에 Broadcast 처리가 됨)
    private async Task PublishDestroy()
    {
        if (PlayerInfo == null) return;
        
        await PlayerInfo.ObjectInfo.Save();

        var isCommonMap = GameMapData.IsCommonMap(MapId);
        var key = isCommonMap ? MapHelper.CreatePartKey(MapId, PlayerInfo.ObjectInfo.CurrentCell) : MapHelper.CreatePartKey(MapId, MapSubId);
        var manageServer = isCommonMap ? MapHelper.GetManageServerId(key) : MapHelper.GetManageServerId(MapSubId);
        var subject = SubjectHelper.GetDestroyObjectSubject(PlayerInfo.ObjectInfo, manageServer);
        var message = MessagePackSerializer.Serialize((key, PlayerInfo.ObjectInfo.GetGameObjectKey()));

        natsClient.Publish(subject, message);
    }
}