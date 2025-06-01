// ReSharper disable PrivateFieldCanBeConvertedToLocalVariable

using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using network.managers;
using network.packets;
using user_server.players;

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
    
    private readonly ICacheHelper _cacheHelper;
    private readonly ILogger _logger;

    private readonly SendPacketDelegate _sendToClient;
    private readonly BroadcastDelegate<PlayerInfo> _broadcastPlayerInfo;
    private readonly BroadcastSocialActionDelegate _broadcastSocialAction;
    private readonly BroadcastTakeDamageDelegate _broadcastTakeDamage;
    private readonly BroadcastDestroyDelegate _broadcastDestroy;
    
    private readonly PlayerQuest _playerQuest;
    private readonly PlayerInventory _playerInventory;
    private readonly PlayerMailBox _playerMailBox;
    private readonly PlayerMovement _playerMovement;
    private readonly PlayerProgress _playerProgress;
    private readonly PlayerCamp _playerCamp;
    private readonly PlayerMap _playerMap;
    private readonly PlayerCraft _playerCraft;
    private readonly PlayerExplore _playerExplore;
    private readonly PlayerInfo _playerInfo;

    public PlayerController(GameUser user, PlayerInfo playerInfo)
    {
        _cacheHelper = user.CacheHelper;
        _logger = user.Logger;
        
        _sendToClient = user.Send;
        _broadcastPlayerInfo = user.BroadcastUpdateInfo;
        _broadcastSocialAction = user.BroadcastSocialAction;
        _broadcastTakeDamage = user.BroadcastTakeDamage;
        _broadcastDestroy = user.BroadcastObjectDestroy;
        
        _playerInfo = playerInfo;
        _playerInfo.ObjectInfo.CurrentCell = _playerInfo.ObjectInfo.TargetCell;
        _playerInfo.State = PlayerState.IDLE;
        
        _playerProgress = new PlayerProgress(user);
        _playerQuest = new PlayerQuest(user, _playerInfo);
        _playerInventory = new PlayerInventory(user, _playerInfo, _playerQuest);
        _playerMailBox = new PlayerMailBox(user, _playerInfo);
        _playerMovement = new PlayerMovement(user, _playerInfo);
        _playerCamp = new PlayerCamp(user, _playerInfo);
        _playerMap = new PlayerMap(user, _playerInfo);
        _playerCraft = new PlayerCraft(user, playerInfo, _playerQuest, _playerInventory, _playerProgress);
        _playerExplore = new PlayerExplore(user, playerInfo, _playerQuest, _playerInventory, _playerProgress);
    }
    
    public long PlayerId => _playerInfo.PlayerId;
    public string PlayerName => _playerInfo.Name;
    public string ObjectKey => _playerInfo.ObjectInfo.GetGameObjectKey();
    
    public (MapId, long, Cell, bool) CurrentMapInfo => _playerMap.CurrentMapInfo;
    public (MapId, Cell) LastMapInfo => _playerMap.LastMapInfo;
    
    public bool IsInvalidAction(Protocol protocolId) => ActionProtocol.Contains(protocolId) && ActionState.Contains(_playerInfo.State);
    public async Task RequestMove(C_TO_U_MOVE body) => await _playerMovement.HandleMove(body);
    public async Task Wear(C_TO_U_WEAR_ITEM body) => await _playerInventory.RequestWearItem(body);
    public async Task Use(C_TO_U_USE_ITEM body) => await _playerInventory.RequestUseItem(body);
    public async Task SendMail(MailInfo mailInfo) => await _playerMailBox.SendMail(mailInfo);
    public async Task StartQuest(int questId, List<QuestInfo>? updateQuests = null) => await _playerQuest.StartQuest(questId, updateQuests);
    public void SendCurrentItems() => _playerInventory.SendCurrentItems();
    public async Task SendCurrentMails() => await _playerMailBox.SendCurrentMails();
    public void SendCurrentQuests() => _playerQuest.SendCurrentQuests();
    public async Task Encamp(C_TO_U_ENCAMP body) => await _playerCamp.Encamp(body);
    public async Task Decamp() => await _playerCamp.Decamp();
    public async Task PutItem(C_TO_U_ITEM_PUT body) => await _playerCamp.PutItem(body);
    public async Task IncreaseQuestCount(C_TO_U_QUEST_INCREASE body) => await _playerQuest.IncreaseQuestCount(body);
    public async Task CompleteQuest(C_TO_U_QUEST_SUCCESS body) => await _playerQuest.CompleteQuest(body);
    public async Task ReceiveMail(C_TO_U_MAIL_RECEIVE body) => await _playerMailBox.ReceiveMail(body);
    public async Task ChangeMap(C_TO_U_CHANGE_MAP body) => await _playerMap.ChangeMap(body);
    public async Task EnterMap(MapId mapId, Cell spawnPosition, bool isFlip, bool isLogin) => await _playerMap.EnterMap(mapId, spawnPosition, isFlip, isLogin);
    public async Task EnterCamp(long mapSubId) => await _playerMap.EnterCamp(mapSubId);
    public async Task Craft(C_TO_U_CRAFT body) => await _playerCraft.Craft(body);
    public async Task Explore(C_TO_U_EXPLORE body) => await _playerExplore.Explore(body);
    public async Task Spawn() => await _playerMovement.Spawn();
    
    public async Task UpdateBoost(C_TO_U_BOOST body)
    {
        _logger.LogWarning("boostType: {body.boostType}, active: {body.active}", body.BoostType, body.IsActive);
        if (!body.IsActive)
        {
            _playerInfo.Boosts.Remove(body.BoostType);
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
    
    public async Task SubscribeEnvironment(G_TO_U_ENVIRONMENT body)
    {
        const int damage = 2000;
        if (_playerInfo.State == PlayerState.SLEEP)
        {
            return;
        }

        if (_playerInfo.QuestDiary.QuestDict.TryGetValue(100000007, out var questInfo))
        {
            if (questInfo.State != QuestState.END)
            {
                return;
            }
        }

        if (_playerInfo.WearItemIdList.Contains(107000001))
        {
            return;
        }
        
        _playerInfo.Hp = Math.Max(0, _playerInfo.Hp - damage);
        if (_playerInfo.Hp <= 0)
        {
            _playerInfo.State = PlayerState.SLEEP;
        }

        await _playerInfo.Save(_cacheHelper);
        
        _broadcastPlayerInfo(_playerInfo);
        _broadcastTakeDamage(_playerInfo, DamageType.DARK, damage);
    }

    public async Task SetName(C_TO_U_SET_NAME body)
    {
        _playerInfo.Name = body.Name;
        await _playerInfo.Save(_cacheHelper);
        
        using var packet = PacketMaker.U_TO_C_SET_NAME(ErrorCode.SUCCESS, _playerInfo);
        _sendToClient(packet);
        _broadcastPlayerInfo(_playerInfo);
    }

    public async Task Dispose()
    {
        _broadcastDestroy(_playerInfo.ObjectInfo);
        await _playerInfo.Save(_cacheHelper);
        await _playerCamp.Decamp();
        _playerMovement.Dispose();
        _playerProgress.Dispose();
    }
}
