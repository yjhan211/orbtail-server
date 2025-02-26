// ReSharper disable PrivateFieldCanBeConvertedToLocalVariable

using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.infrastructure;
using network.interfaces;
using network.managers;
using network.packets;
using RedLockNet.SERedis;
using user_server.controllers.player;
using user_server.managers;
using user_server.progress;

namespace user_server.controllers;

public class PlayerController
{
    private static readonly IReadOnlyList<Protocol> ActionProtocol = new List<Protocol>
    {
        Protocol.C_TO_U_MOVE,
        Protocol.C_TO_U_WEAR_ITEM,
    };
    
    private static readonly IReadOnlyList<PlayerState> ActionState = new List<PlayerState>
    {
        PlayerState.CRAFT_1,
        PlayerState.EXPLORE_1,
    };
    
    private readonly NatsClient _natsClient;
    private readonly CacheHelper _cacheHelper;
    private readonly RedLockFactory _redLock;
    
    private readonly LogManager _logManager;
    private readonly UpdateObjectManager _updateObjectManager;

    private readonly SendPacketDelegate _sendToClient;
    private readonly BroadcastDelegate<PlayerInfo> _broadcastPlayerInfo;
    private readonly BroadcastDelegate<ExploreTargetInfo> _broadcastExploreTargetInfo;
    private readonly BroadcastDestroyDelegate _broadcastDestroy;
    private readonly BroadcastSocialActionDelegate _broadcastSocialAction;
    
    private readonly PlayerQuest _playerQuest;
    private readonly PlayerInventory _playerInventory;
    private readonly PlayerMailBox _playerMailBox;
    private readonly PlayerMovement _playerMovement;
    private readonly PlayerProgress _playerProgress;
    private readonly PlayerCamp _playerCamp;
    
    private readonly PlayerInfo _playerInfo;

    public PlayerController(GameUser user, PlayerInfo playerInfo)
    {
        _natsClient = user.NatsClient;
        _cacheHelper = user.CacheHelper;
        _redLock = user.RedLock;
        
        _logManager = user.LogManager;
        _updateObjectManager = user.UpdateObjectManager;

        _sendToClient = user.Send;
        _broadcastPlayerInfo = user.BroadcastUpdateInfo;
        _broadcastExploreTargetInfo = user.BroadcastUpdateInfo;
        _broadcastDestroy = user.BroadcastObjectDestroy;
        _broadcastSocialAction = user.BroadcastSocialAction;
        
        _playerInfo = playerInfo;
        _playerInfo.ObjectInfo.CurrentCell = _playerInfo.ObjectInfo.TargetCell;
        _playerInfo.State = PlayerState.IDLE;
        
        _playerProgress = new PlayerProgress(user);
        _playerQuest = new PlayerQuest(user, _playerInfo);
        _playerInventory = new PlayerInventory(user, _playerInfo, _playerQuest);
        _playerMailBox = new PlayerMailBox(user, _playerInfo, _playerInventory);
        _playerMovement = new PlayerMovement(user, _playerInfo);
        _playerCamp = new PlayerCamp(user, _playerInfo);
    }
    
    public long PlayerId => _playerInfo.PlayerId;
    public string PlayerName => _playerInfo.Name;
    public string ObjectKey => _playerInfo.ObjectInfo.GetGameObjectKey();
    public (MapId, long, Cell, bool) CurrentMapInfo => (_playerInfo.ObjectInfo.MapId, _playerInfo.ObjectInfo.MapSubId, _playerInfo.ObjectInfo.CurrentCell, _playerInfo.ObjectInfo.IsFlip);
    public (MapId, Cell) LastMapInfo => (_playerInfo.LastMapId, _playerInfo.LastCell);
    public bool IsInvalidAction(Protocol protocolId) => ActionProtocol.Contains(protocolId) && ActionState.Contains(_playerInfo.State);
    public async Task RequestMove(C_TO_U_MOVE body) => await _playerMovement.HandleMove(body);
    public async Task Wear(C_TO_U_WEAR_ITEM body) => await _playerInventory.RequestWearItem(body);
    public async Task Use(C_TO_U_USE_ITEM body) => await _playerInventory.RequestUseItem(body);
    public async Task SendMail(MailInfo mailInfo) => await _playerMailBox.SendMail(mailInfo);
    public async Task StartQuest(int questId, List<QuestInfo>? updateQuests = null) => await _playerQuest.StartQuest(questId, updateQuests);
    public void SendCurrentItems() => _playerInventory.SendCurrentItems();
    public async Task SendCurrentMails() => await _playerMailBox.SendCurrentMails();
    public async Task SendCurrentQuests() => await _playerQuest.SendCurrentQuests();
    public async Task Encamp(C_TO_U_ENCAMP body) => await _playerCamp.Encamp(body);
    public async Task Decamp() => await _playerCamp.Decamp();
    public async Task PutItem(C_TO_U_ITEM_PUT body) => await _playerCamp.PutItem(body);
    public async Task IncreaseQuestCount(C_TO_U_QUEST_INCREASE body) => await _playerQuest.IncreaseQuestCount(body);
    public async Task CompleteQuest(C_TO_U_QUEST_SUCCESS body) => await _playerQuest.CompleteQuest(body);
    public async Task ReceiveMail(C_TO_U_MAIL_RECEIVE body) => await _playerMailBox.ReceiveMail(body);
    
    public async Task ChangeMap(C_TO_U_CHANGE_MAP body)
    {
        if (body.MapId == MapId.Camp)
        {
            _playerInfo.LastMapId = _playerInfo.ObjectInfo.MapId;
            _playerInfo.LastMapSubId = _playerInfo.ObjectInfo.MapSubId;
            _playerInfo.LastCell = _playerInfo.ObjectInfo.CurrentCell.Clone();
            await _playerInfo.Save(_cacheHelper);
            
            var serverId = MapHelper.GetManageServerId(_playerInfo.ObjectInfo.MapSubId);
            var subject = SubjectHelper.GetEnterInstanceSubject(serverId);
            var publishObj = MessagePackSerializer.Serialize((_playerInfo.ObjectInfo.GetGameObjectKey(), body.MapId, body.MapSubId, false));
            _natsClient.Publish(subject, publishObj);
            return;
        }
        
        if (_playerInfo.ObjectInfo.MapId == MapId.Camp)
        {
            await PublishDestroy();
            await EnterMap(_playerInfo.CampInfo.ObjectInfo.MapId, _playerInfo.CampInfo.ObjectInfo.CurrentCell, false, false);
            
            _playerInfo.LastMapId = _playerInfo.ObjectInfo.MapId;
            _playerInfo.LastMapSubId = _playerInfo.ObjectInfo.MapSubId;
            _playerInfo.LastCell = _playerInfo.ObjectInfo.CurrentCell.Clone();
            await _playerInfo.Save(_cacheHelper);
            return;
        }
        
        var changeMapInfo = GameMapData.GetPortalOrNull(_playerInfo.ObjectInfo, _playerInfo.IsTutorial);
        if (changeMapInfo == null)
        {
            return;
        }
        
        // 기존 맵에 삭제 요청
        await PublishDestroy();
        await EnterMap(changeMapInfo.Value.mapId, changeMapInfo.Value.spawnPosition, changeMapInfo.Value.isFlip, false);
        
        _playerInfo.LastMapId = _playerInfo.ObjectInfo.MapId;
        _playerInfo.LastMapSubId = _playerInfo.ObjectInfo.MapSubId;
        _playerInfo.LastCell = _playerInfo.ObjectInfo.CurrentCell.Clone();
        await _playerInfo.Save(_cacheHelper);
    }
    
    public async Task EnterMap(MapId mapId, Cell spawnPosition, bool isFlip, bool isLogin)
    {
        _playerInfo.ObjectInfo.MapId = mapId;
        _playerInfo.ObjectInfo.MapSubId = GameMapData.IsCommonMap(mapId) ? 0 : GetInstanceMapSubId();
        _playerInfo.ObjectInfo.CurrentCell = spawnPosition;
        _playerInfo.ObjectInfo.TargetCell = spawnPosition;
        _playerInfo.ObjectInfo.IsFlip = isFlip;
        await _playerInfo.ObjectInfo.Save(_cacheHelper);

        if (GameMapData.IsCommonMap(_playerInfo.ObjectInfo.MapId) && !isLogin)
        {
            using var packet = PacketMaker.U_TO_C_CHANGE_MAP(_playerInfo.LastMapId, _playerInfo.ObjectInfo.MapId, _playerInfo.ObjectInfo.MapSubId, _playerInfo.ObjectInfo.CurrentCell, _playerInfo.ObjectInfo.IsFlip);
            _sendToClient(packet);
            return;
        }

        var serverId = MapHelper.GetManageServerId(_playerInfo.ObjectInfo.MapSubId);
        var subject = SubjectHelper.GetEnterInstanceSubject(serverId);
        var publishObj = MessagePackSerializer.Serialize((_playerInfo.ObjectInfo.GetGameObjectKey(), _playerInfo.ObjectInfo.MapId, _playerInfo.ObjectInfo.MapSubId, isLogin));
        _natsClient.Publish(subject, publishObj);
    }

    public async Task EnterCamp(long mapSubId)
    {
        if (_playerInfo == null)
        {
            throw new Exception("PlayerInfo is null");
        }

        _playerInfo.ObjectInfo.MapId = MapId.Camp;
        _playerInfo.ObjectInfo.MapSubId = mapSubId;

        var campMapInfo = GameMapData.GetMapInfo(_playerInfo.ObjectInfo.MapId);
        var (spawnPosition, isFlip) = campMapInfo.GetInitialPosition(MapId.None);
        _playerInfo.ObjectInfo.CurrentCell = spawnPosition;
        _playerInfo.ObjectInfo.TargetCell = spawnPosition;
        _playerInfo.ObjectInfo.IsFlip = isFlip;

        await _playerInfo.Save(_cacheHelper);
    }

    private long GetInstanceMapSubId()
    {
        // TODO 동아리
        return _playerInfo.ObjectInfo.ObjectId;
    }

    public async Task Spawn()
    {
        await _playerMovement.Spawn();
    }

    public async Task RequestMove(GameUser user, C_TO_U_MOVE body)
    {
        await _playerMovement.HandleMove(body);
    }

    public async Task UpdateBoost(C_TO_U_BOOST body)
    {
        if (_playerInfo.Boosts.TryGetValue(body.BoostType, out var boost))
        {
            _playerInfo.Boosts.Remove(boost);
        }
        else
        {
            _playerInfo.Boosts.Add(body.BoostType);
        }

        await _playerInfo.Save(_cacheHelper);
        _broadcastPlayerInfo(_playerInfo);
    }

    public async Task SocialAction(C_TO_U_SOCIAL_ACTION body)
    {
        switch (body.SocialActionType)
        {
            case SocialActionType.SITGROUND:
                _playerInfo.State = _playerInfo.State == PlayerState.IDLE ? PlayerState.SITGROUND : PlayerState.IDLE;
                await _playerInfo.Save(_cacheHelper);
                _broadcastPlayerInfo(_playerInfo);
                break;

            default:
                _broadcastSocialAction(_playerInfo, body.SocialActionType);
                break;
        }
    }

    public async Task SetName(C_TO_U_SET_NAME body)
    {
        _playerInfo.Name = body.Name;
        await _playerInfo.Save(_cacheHelper);
        
        using var packet = PacketMaker.U_TO_C_SET_NAME(ErrorCode.SUCCESS, _playerInfo);
        _sendToClient(packet);
        _broadcastPlayerInfo(_playerInfo);
    }
    
    public async Task Craft(C_TO_U_CRAFT body)
    {
        await using (await PlayerInfo.Lock(_redLock, _playerInfo.PlayerId))
        {
            var craftData = GameCraftData.Get(body.CraftId);
            if (_playerInfo.Stamina < craftData.Stamina)
            {
                using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
                _sendToClient(errorPacket);
                return;
            }

            if (!_playerInfo.CraftInfo.Manuals.Contains(craftData.ManualId))
            {
                using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
                _sendToClient(errorPacket);
                return;
            }
            
            var craftProgressInfo = new CraftProgressInfo(body.CraftId, DateTime.Now.AddSeconds(craftData.Seconds));
            _playerProgress.AddProgressItem(craftProgressInfo, async trackable =>
            {
                var progressInfo = (CraftProgressInfo)trackable;
                await OnCraftComplete(progressInfo);
            });
            
            _playerInfo.State = PlayerState.CRAFT_1;
            _playerInfo.Stamina = Math.Clamp(_playerInfo.Stamina - craftData.Stamina, 0, 100);
            await _playerInfo.Save(_cacheHelper);
        }
        
        using var packet = PacketMaker.U_TO_C_CRAFT(ErrorCode.SUCCESS);
        _sendToClient(packet);
        _broadcastPlayerInfo(_playerInfo);
    }
    
    private async Task OnCraftComplete(IProgressTrackable trackable)
    {
        if (trackable is not CraftProgressInfo craftProgress)
        {
            return;
        }

        var craftId = craftProgress.CraftId;
        
        var updateItems = new List<ItemInfo>();
        var updateQuests = new List<QuestInfo>();
        await using (await PlayerInfo.Lock(_redLock, _playerInfo.PlayerId))
        {
            var craftData = GameCraftData.Get(craftId);
            var craftTargetItem = await PlayerInventory.CreateItem(_cacheHelper, craftData.TargetItem, 1);

            var addItem = _playerInfo.InventoryInfo.AddItem(craftTargetItem);
            updateItems.Add(addItem);
            
            foreach (var (itemId, count) in craftData.RequireItems)
            {
                var deleteItem = _playerInfo.InventoryInfo.DeleteItemById(itemId, count);
                if (deleteItem == null)
                {
                    throw new Exception("cannot find delete item");
                }
                updateItems.Add(deleteItem);
            }
            _playerInfo.State = PlayerState.IDLE;
            await _playerInfo.Save(_cacheHelper);
            
            // 퀘스트 갱신
            switch (craftId)
            {
                case 1:
                    await _playerQuest.IncreaseQuestCount(100000010, 1, updateQuests);
                    break;
                case 3:
                    await _playerQuest.IncreaseQuestCount(100000013, 1, updateQuests);
                    break;
            }
        }
        
        using var packet = PacketMaker.U_TO_C_CRAFT_COMPLETE(true);
        _sendToClient(packet);
        _broadcastPlayerInfo(_playerInfo);
        _playerInventory.SendUpdateItems(updateItems);

        foreach (var quest in updateQuests)
        {
            using var questPacket = PacketMaker.U_TO_C_QUEST_UPDATE(quest);
            _sendToClient(questPacket);
        }
    }
    
    public async Task Explore(C_TO_U_EXPLORE body)
    {
        ExploreTargetInfo? exploreTargetInfo;
        await using (await PlayerInfo.Lock(_redLock, _playerInfo.PlayerId))
        {
            if (_playerInfo.Stamina <= 0)
            {
                using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                _sendToClient(errorPacket);
                return;
            }

            await using (await ExploreTargetInfo.Lock(_redLock, body.ExploreTargetUid))
            {
                exploreTargetInfo = await ExploreTargetInfo.Load(_cacheHelper, body.ExploreTargetUid);
                if (exploreTargetInfo == null)
                {
                    using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                    _sendToClient(errorPacket);
                    return;
                }

                if (exploreTargetInfo.PlayerId != 0)
                {
                    using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.ALREADY_ANOTHER_USE_SKILL);
                    _sendToClient(errorPacket);
                    return;
                }
                
                // if (2 < user.CurrentCell.GetDistance(exploreTargetInfo.ObjectInfo.CurrentCell))
                // {
                //     using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                //     user.Send(errorPacket);
                //     return;
                // }

                exploreTargetInfo.PlayerId = _playerInfo.PlayerId;
                exploreTargetInfo.EndTimestamp = DateTime.UtcNow.AddSeconds(GameRuleData.SkillCompleteTime);
                
                var exploreProgressInfo = new ExploreProgressInfo(exploreTargetInfo);
                _playerProgress.AddProgressItem(exploreProgressInfo, async trackable =>
                {
                    var progressInfo = (ExploreProgressInfo)trackable;
                    await OnExploreComplete(progressInfo);
                });
                await exploreTargetInfo.Save(_cacheHelper);
            }

            _playerInfo.State = PlayerState.EXPLORE_1;
            _playerInfo.Stamina -= 5;
            await _playerInfo.Save(_cacheHelper);
        }

        using var packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.SUCCESS);
        _sendToClient(packet);
        _broadcastPlayerInfo(_playerInfo);
        _broadcastExploreTargetInfo(exploreTargetInfo);
    }

    private async Task OnExploreComplete(ExploreProgressInfo exploreProgressInfo)
    {
        var updateItems = new List<ItemInfo>();
        var updateQuests = new List<QuestInfo>();
        var exploreTargetInfo = exploreProgressInfo.ExploreTargetInfo;
        await using (await PlayerInfo.Lock(_redLock, _playerInfo.PlayerId))
        {
            // TODO 능력치 기반한 확률
            var exploreTargetData = GameExploreTargetData.Get(exploreTargetInfo.ExploreTargetId);
            if (exploreTargetData.Reusable)
            {
                // 조사대상 수집 불가능하도록 TODO 재충전
                exploreTargetInfo.PlayerId = -1;
                await exploreTargetInfo.Save(_cacheHelper);
                _broadcastExploreTargetInfo(exploreTargetInfo);
            }
            else
            {
                // 조사대상 삭제
                await exploreTargetInfo.Delete(_cacheHelper);
                _broadcastDestroy(exploreTargetInfo.ObjectInfo);
            }

            // 수집 결과 지급
            var random = new Random();
            var rewardItemId = exploreTargetData.RewardItemPool[random.Next(0, exploreTargetData.RewardItemPool.Count)];
            var rewardItem = await PlayerInventory.CreateItem(_cacheHelper, rewardItemId, 1);
            
            var updateItem = _playerInfo.InventoryInfo.AddItem(rewardItem);
            updateItems.Add(updateItem);

            _playerInfo.State = PlayerState.IDLE;
            await _playerInfo.Save(_cacheHelper);
            
            // 퀘스트 갱신
            switch (exploreTargetInfo.ExploreTargetId)
            {
                case 1:
                case 2:
                case 3:
                    await _playerQuest.IncreaseQuestCount(100000004, 1, updateQuests);
                    break;
                case 4:
                case 5: 
                case 6:
                    await _playerQuest.IncreaseQuestCount(100000008, 1, updateQuests);
                    break;
                case 7:
                    await _playerQuest.IncreaseQuestCount(100000008, 1, updateQuests);
                    await _playerQuest.StartQuest(100000009, updateQuests);
                    break;
                case 8:
                case 9: 
                case 10: 
                case 11:
                    await _playerQuest.IncreaseQuestCount(100000012, 1, updateQuests);
                    break;
            }
        }
        using var packet = PacketMaker.U_TO_C_EXPLORE_COMPLETE(true);
        _sendToClient(packet);
        _broadcastPlayerInfo(_playerInfo);
        _playerInventory.SendUpdateItems(updateItems);

        foreach (var updateQuest in updateQuests)
        {
            using var questPacket = PacketMaker.U_TO_C_QUEST_UPDATE(updateQuest);
            _sendToClient(questPacket);
        }
    }

    public async Task Dispose()
    {
        await PublishDestroy();
        _playerMovement.Dispose();
        _playerProgress.Dispose();
        // await _campController.Decamp();
    }

    // 접속 종료 시 자신의 object_info 삭제 요청 (PublishLeave랑 다른 점 - 후에 Broadcast 처리가 됨)
    private async Task PublishDestroy()
    {
        await _playerInfo.ObjectInfo.Save(_cacheHelper);

        var isCommonMap = GameMapData.IsCommonMap(_playerInfo.ObjectInfo.MapId);
        var key = isCommonMap ? MapHelper.CreatePartKey(_playerInfo.ObjectInfo.MapId, _playerInfo.ObjectInfo.CurrentCell) : MapHelper.CreatePartKey(_playerInfo.ObjectInfo.MapId, _playerInfo.ObjectInfo.MapSubId);
        var manageServer = isCommonMap ? MapHelper.GetManageServerId(key) : MapHelper.GetManageServerId(_playerInfo.ObjectInfo.MapSubId);
        var subject = SubjectHelper.GetDestroyObjectSubject(_playerInfo.ObjectInfo, manageServer);
        var message = MessagePackSerializer.Serialize((key, _playerInfo.ObjectInfo.GetGameObjectKey()));

        _natsClient.Publish(subject, message);
    }
}