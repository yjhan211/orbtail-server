using System.Threading.Channels;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.helpers;
using network.infrastructure;
using network.interfaces;
using network.managers;
using network.packets;
using network.utils;
using RedLockNet.SERedis;
using user_server.controllers;
using user_server.handlers;
using user_server.managers;

namespace user_server;

public partial class GameUser : IPeer
{
    private static readonly IReadOnlyList<Protocol> NonAuthProtocol = new List<Protocol>
    {
        Protocol.C_TO_U_HEART_BEAT,
        Protocol.C_TO_U_LOGIN
    };

    private static readonly IReadOnlyList<Protocol> ActionProtocol = new List<Protocol>
    {
        Protocol.C_TO_U_MOVE,
        Protocol.C_TO_U_WEAR_ITEM,
        Protocol.C_TO_U_USE_SKILL
    };

    public static readonly IReadOnlyList<PlayerState> ActionState = new List<PlayerState>
    {
    };

    private readonly CancellationTokenSource _cts;
    private readonly ProgressManager _progressManager;
    
    private readonly Action<GameUser> _onLeaveCallback;
    private readonly UserToken _token;
    private readonly UpdateObjectManager _updateObjectManager;
    private readonly SemaphoreSlim _userLock;
    public readonly NatsClient NatsClient;
    public readonly RedLockFactory RedLock;
    public readonly LogManager LogManager;
    public readonly PlayerManager PlayerManager;

    public GameUser(UserToken token, RedLockFactory redLockFactory, NatsClient natsClient, LogManager logManager, Action<GameUser> onLeaveCallback)
    {
        _token = token;
        _token.SetPeer(this);

        RedLock = redLockFactory;
        NatsClient = natsClient;
        LogManager = logManager;
        _userLock = new SemaphoreSlim(1);
        _cts = new CancellationTokenSource();

        _progressManager = new ProgressManager(LogManager);
        var updateObjectChannel = Channel.CreateUnbounded<GameObjectInfo>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        _updateObjectManager = new UpdateObjectManager(_cts, LogManager, Send, updateObjectChannel);
        PlayerManager = new PlayerManager(LogManager, NatsClient, Send, _updateObjectManager);
        _onLeaveCallback = onLeaveCallback;

        LogManager.WriteInfoLog("Create GameUser Success!");
    }

    public long PlayerId => PlayerManager.PlayerId;
    public PlayerState PlayerState => PlayerManager.State;
    public Cell CurrentCell => PlayerManager.CurrentCell ?? new(0, 0);

    public async Task OnMessageFromClient(Const<byte[]> buffer)
    {
        try
        {
            await _userLock.WaitAsync();

            using var packet = Packet.Create(buffer);
            var protocolId = (Protocol)packet.PopProtocolId();
            var playerId = packet.PopPlayerId();
            var body = packet.PopBody();

            LogManager.WriteDebugLog($"playerId: {playerId} | PROTOCOL: {protocolId}");

            if (NonAuthProtocol.Contains(protocolId))
            {
                switch (protocolId)
                {
                    case Protocol.C_TO_U_HEART_BEAT:
                        HeartBeat();
                        break;
                    case Protocol.C_TO_U_LOGIN:
                        await HandleMessage<C_TO_U_LOGIN>(body, Login);
                        break;
                }

                return;
            }

            if (PlayerManager.State == PlayerState.NONE)
            {
                throw new Exception("invalid PlayerState");
            }

            if (ActionProtocol.Contains(protocolId) && ActionState.Contains(PlayerManager.State))
            {
                throw new Exception($"in action. {PlayerManager.PlayerId}");
            }

            switch (protocolId)
            {
                case Protocol.C_TO_U_CHANGE_MAP_SUCCESS:
                    await PlayerManager.Spawn();
                    break;
                case Protocol.C_TO_U_CHAT_LOG:
                    await ChatController.GetChatHistory(this, ChatType.ALL);
                    break;
                case Protocol.C_TO_U_MOVE:
                    await HandleMessage<C_TO_U_MOVE>(body, PlayerManager.RequestMove);
                    break;
                case Protocol.C_TO_U_PLAYER_INFO:
                    await HandleMessage<C_TO_U_PLAYER_INFO>(body, PlayerController.GetPlayerInfo);
                    break;
                case Protocol.C_TO_U_OBJECT_INFO:
                    await HandleMessage<C_TO_U_OBJECT_INFO>(body, _updateObjectManager.GetObjectInfo);
                    break;
                case Protocol.C_TO_U_EXPLORE_TARGET_INFO:
                    await HandleMessage<C_TO_U_EXPLORE_TARGET_INFO>(body, ExploreController.GetExploreTargetInfo);
                    break;
                case Protocol.C_TO_U_JOB_RESOURCE_INFO:
                    // await HandleMessage<C_TO_U_JOB_RESOURCE_INFO>(body, JobController.GetJobResourceInfo);
                    break;
                case Protocol.C_TO_U_UPGRADE_JOB:
                    // await HandleMessage<C_TO_U_UPGRADE_JOB>(body, JobController.UpgradeJob);
                    break;
                case Protocol.C_TO_U_WEAR_ITEM:
                    await HandleMessage<C_TO_U_WEAR_ITEM>(body, InventoryController.RequestWearItem);
                    break;
                case Protocol.C_TO_U_USE_ITEM:
                    await HandleMessage<C_TO_U_USE_ITEM>(body, InventoryController.RequestUseItem);
                    break;
                case Protocol.C_TO_U_CHANGE_MAP:
                    await PlayerManager.ChangeMap();
                    break;
                case Protocol.C_TO_U_EXPLORE:
                    await HandleMessage<C_TO_U_EXPLORE>(body, ExploreController.Explore);
                    break;
                case Protocol.C_TO_U_USE_SKILL:
                    // await HandleMessage<C_TO_U_USE_SKILL>(body, JobController.UseJobSkill);
                    break;
                case Protocol.C_TO_U_CHAT_MSG:
                    await HandleMessage<C_TO_U_CHAT_MSG>(body, ChatController.SendChat);
                    break;
                case Protocol.C_TO_U_CREATE_LAB:
                    await HandleMessage<C_TO_U_CREATE_LAB>(body, LabController.CreateLab);
                    break;
                case Protocol.C_TO_U_UPGRADE_RESEARCH:
                    await HandleMessage<C_TO_U_UPGRADE_RESEARCH>(body, LabController.UpgradeResearch);
                    break;
                case Protocol.C_TO_U_CRAFT:
                    await HandleMessage<C_TO_U_CRAFT>(body, CraftController.Craft);
                    break;
                case Protocol.C_TO_U_WRITE_LAB_HIRE:
                    await HandleMessage<C_TO_U_WRITE_LAB_HIRE>(body, LabController.WriteLabHire);
                    break;
                case Protocol.C_TO_U_LAB_HIRE_LIST:
                    await LabController.LabHireList(this);
                    break;
                case Protocol.C_TO_U_JOIN_LAB:
                    await HandleMessage<C_TO_U_JOIN_LAB>(body, LabController.JoinLab);
                    break;
                case Protocol.C_TO_U_LAB_INVENTORY:
                    await InventoryController.GetLabInventory(this);
                    break;
                case Protocol.C_TO_U_LAB_INVENTORY_ADD_ITEM:
                    await HandleMessage<C_TO_U_LAB_INVENTORY_ADD_ITEM>(body, InventoryController.AddLabItem);
                    break;
                case Protocol.C_TO_U_LAB_INVENTORY_TAKE_ITEM:
                    await HandleMessage<C_TO_U_LAB_INVENTORY_TAKE_ITEM>(body, InventoryController.TakeLabItem);
                    break;
                case Protocol.C_TO_U_ENCAMP:
                    // await HandleMessage<C_TO_U_ENCAMP>(body, JobController.Encamp);
                    break;
                case Protocol.C_TO_U_DECAMP:
                    // await JobController.Decamp(this);
                    break;
                case Protocol.C_TO_U_ADD_SELL_ITEM:
                    // await HandleMessage<C_TO_U_ADD_SELL_ITEM>(body, JobController.AddSellItem);
                    break;
                case Protocol.C_TO_U_DELETE_SELL_ITEM:
                    // await HandleMessage<C_TO_U_DELETE_SELL_ITEM>(body, JobController.DeleteSellItem);
                    break;
                case Protocol.C_TO_U_BUY_ITEM:
                    // await HandleMessage<C_TO_U_BUY_ITEM>(body, JobController.BuyItem);
                    break;
                case Protocol.C_TO_U_CAMP_INFO:
                    await HandleMessage<C_TO_U_CAMP_INFO>(body, CampController.GetCampInfo);
                    break;
                case Protocol.C_TO_U_SET_NAME:
                    await HandleMessage<C_TO_U_SET_NAME>(body, PlayerManager.SetName);
                    break;
                case Protocol.C_TO_U_BOOST:
                    await HandleMessage<C_TO_U_BOOST>(body, PlayerManager.UpdateBoost);
                    break;
                case Protocol.C_TO_U_SOCIAL_ACTION:
                    await HandleMessage<C_TO_U_SOCIAL_ACTION>(body, PlayerManager.SocialAction);
                    break;
                case Protocol.C_TO_U_QUEST_INCREASE:
                    await HandleMessage<C_TO_U_QUEST_INCREASE>(body, QuestController.IncreaseQuestCount);
                    break;
                case Protocol.C_TO_U_QUEST_SUCCESS:
                    await HandleMessage<C_TO_U_QUEST_INCREASE>(body, QuestController.CompleteQuest);
                    break;
                case Protocol.C_TO_U_MAIL_LIST:
                    await MailBoxController.GetCurrentMailList(this);
                    break;
                case Protocol.C_TO_U_MAIL_RECEIVE:
                    await HandleMessage<C_TO_U_MAIL_RECEIVE>(body, MailBoxController.ReceiveMail);
                    break;
            }
        }
        catch (Exception e)
        {
            LogManager.WriteErrorLog(e);
        }
        finally
        {
            _userLock.Release();
        }
    }

    public void Send(IPacket msg)
    {
        if (msg is not Packet packet) throw new NotImplementedException();

        _token.Send(packet);
    }

    public void OnRemoved()
    {
        LogManager.WriteInfoLog($"GameUser Removed. PlayerId:{PlayerId}");
        _onLeaveCallback(this);
    }

    public async Task SetState(PlayerState state)
    {
        await PlayerManager.SetState(state);
    }

    public async Task SetFlip(DirectionType direction)
    {
        await PlayerManager.SetFlip(direction);
    }

    private async Task HandleMessage<T>(byte[] body, Func<GameUser, T, Task> handleMessage)
    {
        var msg = MessagePackSerializer.Deserialize<T>(body);
        await handleMessage(this, msg);
    }

    private void HandleMessage<T>(byte[] body, Action<GameUser, T> handleMessage)
    {
        var msg = MessagePackSerializer.Deserialize<T>(body);
        handleMessage(this, msg);
    }

    private void HeartBeat()
    {
        using var packet = PacketMaker.U_TO_C_HEART_BEAT(DateTime.UtcNow);
        Send(packet);
    }

    private async Task Login(GameUser _, C_TO_U_LOGIN request)
    {
        if (PlayerManager.State != PlayerState.NONE)
        {
            throw new Exception($"Already Initialized. {PlayerManager.PlayerId}");
        }

        var isDummy = false;
        if (!long.TryParse(request.AccountToken, out var tempPlayerId))
        {
            tempPlayerId = await CacheHelper.Instance.StringIncrementAsync("temp_player_id") + 1000;
            isDummy = true;
        }

        PlayerInfo? playerInfo;
        await using (await PlayerInfo.Lock(RedLock, tempPlayerId))
        {
            var isInit = false;
            var giftItemList = new List<ItemInfo>();

            playerInfo = await PlayerInfo.Load(tempPlayerId);
            if (playerInfo == null)
            {
                isInit = true;
                playerInfo = new PlayerInfo(tempPlayerId, isDummy);

                foreach (var (itemId, count) in GameRuleData.DefaultItemList)
                {
                    var item = await InventoryController.CreateItem(itemId, count);
                    giftItemList.Add(item);
                }

                playerInfo.InventoryInfo.AddItem(giftItemList);
            }

            var environmentHandler = new EnvironmentHandler(LogManager, _cts, RedLock, Send, playerInfo.ObjectInfo);
            await environmentHandler.StartAsync();
            PlayerManager.Initialize(playerInfo, environmentHandler);

            using var duplicatePacket = Packet.Create((int)Protocol.U_TO_U_DUPLICATE);
            NatsClient.Publish(PlayerManager.ObjectInfo!.GetGameObjectKey(), duplicatePacket.ToBytes());

            await playerInfo.Save();
            await playerInfo.ObjectInfo.Save();

            if (isInit)
            {
                var firstMail = await MailBoxController.CreateMail(1);
                await MailBoxController.SendMail(this, firstMail);
                await QuestController.StartQuest(this, 100000001);
                
                // 기본템 입히기
                var defaultTop = giftItemList.First(x => x.ItemId == 104000001);
                var defaultBottom = giftItemList.First(x => x.ItemId == 105000001);
                var defaultShoes = giftItemList.First(x => x.ItemId == 106000001);

                await PlayerManager.Wear(defaultTop.ItemUid);
                await PlayerManager.Wear(defaultBottom.ItemUid);
                await PlayerManager.Wear(defaultShoes.ItemUid);
            }
        }

        NatsClient.Subscribe(PlayerManager.ObjectInfo.GetGameObjectKey(),
            async void (_, message) =>
            {
                try
                {
                    await OnMessageFromSubscribe(message);
                }
                catch (Exception e)
                {
                    LogManager.WriteErrorLog(e);
                }
            });
        
        NatsClient.Subscribe("all", async void (_, message) =>
        {
            try
            {
                await OnMessageFromSubscribe(message);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
        });

        var labInfo = await LabInfo.Load(playerInfo.LabId);

        using var loginPacket = PacketMaker.U_TO_C_LOGIN(playerInfo, labInfo ?? new LabInfo());
        Send(loginPacket);

        // 인벤토리 정보 전송
        InventoryController.SendCurrentItems(this);
        if (labInfo != null) await InventoryController.GetLabInventory(this);
        
        // 우편 정보 전송
        await MailBoxController.GetCurrentMailList(this);
        
        // 퀘스트 정보 전송
        await QuestController.GetCurrentQuestList(this);

        await PlayerManager.EnterMap(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.CurrentCell, playerInfo.ObjectInfo.IsFlip, true);
    }

    public void BroadcastUpdateInfo<T>(T info) where T : IMessagePackObject?
    {
        var objectInfo = info switch
        {
            PlayerInfo p => p.ObjectInfo,
            ExploreTargetInfo e => e.ObjectInfo,
            CampInfo c => c.ObjectInfo,
            _ => throw new ArgumentException($"Unsupported type: {typeof(T)}")
        };
    
        if (GameMapData.IsCommonMap(objectInfo.MapId))
        {
            var partKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.CurrentCell);
            var targetServerList = MapHelper.GetBoundServerList(objectInfo.MapId, objectInfo.CurrentCell);
            foreach (var subject in targetServerList.Select(targetServer => SubjectHelper.GetUpdateInfoSubject(objectInfo, targetServer)))
            {
                NatsClient.Publish(subject, MessagePackSerializer.Serialize((partKey, info)));
            }
            return;
        }
    
        var instancePartKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);
        var manageServer = MapHelper.GetManageServerId(objectInfo.MapSubId);
        var instanceSubject = SubjectHelper.GetUpdateInfoSubject(objectInfo, manageServer);
        
        var serializedInfo = MessagePackSerializer.Serialize(info);
        NatsClient.Publish(instanceSubject, MessagePackSerializer.Serialize((instancePartKey, objectInfo.ObjectType, serializedInfo)));
    }

    public void BroadcastSocialAction(PlayerInfo playerInfo, SocialActionType socialActionType)
    {
        var objectInfo = playerInfo.ObjectInfo;
        var sendTuple = (playerInfo.PlayerId, socialActionType);
        if (GameMapData.IsCommonMap(objectInfo.MapId))
        {
            var partKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.CurrentCell);
            var targetServerList = MapHelper.GetBoundServerList(objectInfo.MapId, objectInfo.CurrentCell);
            foreach (var subject in targetServerList.Select(targetServer => SubjectHelper.GetSocialActionSubject(objectInfo, targetServer)))
            {
                NatsClient.Publish(subject, MessagePackSerializer.Serialize((partKey, sendTuple)));
            }
            return;
        }
    
        var instancePartKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);
        var manageServer = MapHelper.GetManageServerId(objectInfo.MapSubId);
        var instanceSubject = SubjectHelper.GetSocialActionSubject(objectInfo, manageServer);
        NatsClient.Publish(instanceSubject, MessagePackSerializer.Serialize((instancePartKey, sendTuple)));
    }
    
    public void BroadcastObjectDestroy(GameObjectInfo objectInfo)
    {
        var positionKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.CurrentCell);
        var manageServer = MapHelper.GetManageServerId(positionKey);
        var subject = SubjectHelper.GetDestroyObjectSubject(objectInfo.MapId, objectInfo.MapSubId, manageServer);
        var message = MessagePackSerializer.Serialize((positionKey, objectInfo.GetGameObjectKey()));

        NatsClient.Publish(subject, message);
    }

    public void PublishToClients(Packet packet, List<long> userIdList)
    {
        foreach (var userId in userIdList)
            NatsClient.Publish(GameObjectInfo.MakeObjectKey(ObjectType.PLAYER, userId), packet.ToBytes());
    }

    private static async Task SendToGameServer(Packet msg)
    {
        await CacheHelper.Instance.EnqueueAsync("game_server_queue", msg.ToBytes());
    }

    private void RecvDuplicate()
    {
        using var packet = Packet.Create((int)Protocol.U_TO_U_DUPLICATE);
        Send(packet);
        OnRemoved();
    }

    public async Task<UserToken?> Release()
    {
        await _token.LockDisconnect.WaitAsync();
        try
        {
            if (_token.IsReleased) return null;
            _token.IsReleased = true;

            // await JobController.Decamp(this);
            await PlayerManager.Dispose();
            _progressManager.Dispose();
            _updateObjectManager.Dispose();

            using var packet = PacketMaker.U_TO_G_LOGOUT(PlayerId);
            await SendToGameServer(packet);

            await _cts.CancelAsync();
            NatsClient.Close();
            _cts.Dispose();
        }
        catch (Exception ex)
        {
            LogManager.WriteErrorLog(ex);
        }
        finally
        {
            _token.LockDisconnect.Release();
        }

        return _token;
    }
}