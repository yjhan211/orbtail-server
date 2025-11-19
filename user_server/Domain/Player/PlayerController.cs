using user_server.infrastructure.network;
// ReSharper disable PrivateFieldCanBeConvertedToLocalVariable

using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.interfaces;
using network.packets;
using user_server.domain.player;

namespace user_server.domain.player;

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
    private readonly PlayerMap _playerMap;
    private readonly PlayerExplore _playerExplore;
    public readonly PlayerInfo PlayerInfo;

    public PlayerController(GameSession user, PlayerInfo playerInfo)
    {
        _cacheHelper = user.CacheHelper;
        _logger = user.Logger;
        
        _sendToClient = user.Send;
        _broadcastPlayerInfo = user.BroadcastUpdateInfo;
        _broadcastSocialAction = user.BroadcastSocialAction;
        _broadcastTakeDamage = user.BroadcastTakeDamage;
        _broadcastDestroy = user.BroadcastObjectDestroy;
        
        PlayerInfo = playerInfo;
        PlayerInfo.ObjectInfo.CurrentCell = PlayerInfo.ObjectInfo.TargetCell;
        PlayerInfo.State = PlayerState.IDLE;
        
        _playerProgress = new PlayerProgress(user);
        _playerQuest = new PlayerQuest(user, PlayerInfo);
        _playerInventory = new PlayerInventory(user, PlayerInfo, _playerQuest);
        _playerMailBox = new PlayerMailBox(user, PlayerInfo);
        _playerMovement = new PlayerMovement(user, PlayerInfo);
        _playerMap = new PlayerMap(user, PlayerInfo);
        _playerExplore = new PlayerExplore(user, playerInfo, _playerQuest, _playerInventory, _playerProgress);
    }
    
    public long PlayerId => PlayerInfo.PlayerId;
    public string PlayerName => PlayerInfo.Name;
    public string ObjectKey => PlayerInfo.ObjectInfo.GetGameObjectKey();
    
    public (MapId, long, Cell, bool) CurrentMapInfo => _playerMap.CurrentMapInfo;
    public (MapId, Cell) LastMapInfo => _playerMap.LastMapInfo;
    
    public bool IsInvalidAction(Protocol protocolId) => ActionProtocol.Contains(protocolId) && ActionState.Contains(PlayerInfo.State);
    public async Task RequestMove(C_TO_U_MOVE body) => await _playerMovement.HandleMove(body);
    public async Task Wear(C_TO_U_WEAR_ITEM body) => await _playerInventory.RequestWearItem(body);
    public async Task Use(C_TO_U_USE_ITEM body) => await _playerInventory.RequestUseItem(body);
    public async Task SendMail(MailInfo mailInfo) => await _playerMailBox.SendMail(mailInfo);
    public async Task StartQuest(int questId, List<QuestInfo>? updateQuests = null) => await _playerQuest.StartQuest(questId, updateQuests);
    public void SendCurrentItems() => _playerInventory.SendCurrentItems();
    public async Task SendCurrentMails() => await _playerMailBox.SendCurrentMails();
    public void SendCurrentQuests() => _playerQuest.SendCurrentQuests();
    public async Task IncreaseQuestCount(C_TO_U_QUEST_INCREASE body) => await _playerQuest.IncreaseQuestCount(body);
    public async Task CompleteQuest(C_TO_U_QUEST_SUCCESS body) => await _playerQuest.CompleteQuest(body);
    public async Task ReceiveMail(C_TO_U_MAIL_RECEIVE body) => await _playerMailBox.ReceiveMail(body);
    public async Task ChangeMap(C_TO_U_CHANGE_MAP body) => await _playerMap.ChangeMap(body);
    public async Task EnterMap(MapId mapId, Cell spawnPosition, bool isFlip, bool isLogin) => await _playerMap.EnterMap(mapId, spawnPosition, isFlip, isLogin);
    public async Task EnterCamp(long mapSubId) => await _playerMap.EnterCamp(mapSubId);
    public async Task Explore(C_TO_U_EXPLORE body) => await _playerExplore.Explore(body);
    public async Task Spawn() => await _playerMovement.Spawn();

    public async Task SocialAction(C_TO_U_SOCIAL_ACTION body)
    {
        switch (body.SocialActionType)
        {
            case SocialActionType.SITGROUND:
                PlayerInfo.State = PlayerInfo.State == PlayerState.IDLE ? PlayerState.SITGROUND : PlayerState.IDLE;
                await PlayerInfo.Save(_cacheHelper);
                _broadcastPlayerInfo(PlayerInfo);
                break;

            default:
                _broadcastSocialAction(PlayerInfo, body.SocialActionType);
                break;
        }
    }
    
    public async Task SubscribeEnvironment(G_TO_U_ENVIRONMENT body)
    {
        const int damage = 2000;
        if (PlayerInfo.PlayerId > PlayerConstants.DUMMY_PLAYER_ID_THRESHOLD)
        {
            return;
        }
        
        if (PlayerInfo.State == PlayerState.SLEEP)
        {
            return;
        }

        if (PlayerInfo.QuestDiary.QuestDict.TryGetValue(100000007, out var questInfo))
        {
            if (questInfo.State != QuestState.END)
            {
                return;
            }
        }

        if (PlayerInfo.WearItemIdList.Contains(107000001))
        {
            return;
        }
        
        PlayerInfo.Hp = Math.Max(0, PlayerInfo.Hp - damage);
        if (PlayerInfo.Hp <= 0)
        {
            PlayerInfo.State = PlayerState.SLEEP;
        }

        await PlayerInfo.Save(_cacheHelper);
        
        _broadcastPlayerInfo(PlayerInfo);
        _broadcastTakeDamage(PlayerInfo, DamageType.DARK, damage);
    }

    public async Task SetName(C_TO_U_SET_NAME body)
    {
        PlayerInfo.Name = body.Name;
        await PlayerInfo.Save(_cacheHelper);
        
        using var packet = PacketMaker.U_TO_C_SET_NAME(ErrorCode.SUCCESS, PlayerInfo);
        _sendToClient(packet);
        _broadcastPlayerInfo(PlayerInfo);
    }

    public async Task Dispose()
    {
        _broadcastDestroy(PlayerInfo.ObjectInfo);
        await PlayerInfo.Save(_cacheHelper);
        _playerMovement.Dispose();
        _playerProgress.Dispose();
    }
}
