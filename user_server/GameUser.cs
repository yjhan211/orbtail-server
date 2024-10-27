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

    private readonly CancellationTokenSource _cts;
    private readonly LogManager _logManager;
    private readonly Action<GameUser> _onLeaveCallback;
    private readonly PlayerManager _playerManager;
    private readonly ProgressManager _progressManager;
    private readonly UserToken _token;
    private readonly UpdateObjectManager _updateObjectManager;
    private readonly SemaphoreSlim _userLock;
    public readonly NatsClient NatsClient;
    public readonly RedLockFactory RedLock;

    public GameUser(UserToken token, RedLockFactory redLockFactory, NatsClient natsClient, LogManager logManager,
        Action<GameUser> onLeaveCallback)
    {
        _token = token;
        _token.SetPeer(this);

        RedLock = redLockFactory;
        NatsClient = natsClient;
        _logManager = logManager;
        _userLock = new SemaphoreSlim(1);
        _cts = new CancellationTokenSource();

        _progressManager = new ProgressManager(_logManager);
        Channel<GameObjectInfo> updateObjectChannel =
            Channel.CreateUnbounded<GameObjectInfo>(new UnboundedChannelOptions
                { SingleReader = false, SingleWriter = false });
        _updateObjectManager = new UpdateObjectManager(_cts, _logManager, Send, updateObjectChannel);
        _playerManager = new PlayerManager(_logManager, NatsClient, Send, _updateObjectManager);
        _onLeaveCallback = onLeaveCallback;

        _logManager.WriteInfoLog("Create GameUser Success!");
    }

    public long PlayerId => _playerManager.PlayerId;
    public PlayerState PlayerState => _playerManager.State;
    public Cell CurrentCell => _playerManager.CurrentCell;

    public async Task OnMessageFromClient(Const<byte[]> buffer)
    {
        try
        {
            await _userLock.WaitAsync();

            using var packet = Packet.Create(buffer);
            var protocolId = (Protocol)packet.PopProtocolId();
            _ = packet.PopPlayerId();
            var body = packet.PopBody();

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

            if (_playerManager.State == PlayerState.NONE) throw new Exception("invalid PlayerState");

            if (ActionProtocol.Contains(protocolId) && _playerManager.State != PlayerState.IDLE)
                throw new Exception($"in action. {_playerManager.PlayerId}");

            switch (protocolId)
            {
                case Protocol.C_TO_U_CHANGE_MAP_SUCCESS:
                    await _playerManager.Spawn();
                    break;
                case Protocol.C_TO_U_CHAT_LOG:
                    await ChatController.GetChatHistory(this, ChatType.ALL);
                    break;
                case Protocol.C_TO_U_MOVE:
                    await HandleMessage<C_TO_U_MOVE>(body, _playerManager.RequestMove);
                    break;
                case Protocol.C_TO_U_PLAYER_INFO:
                    await HandleMessage<C_TO_U_PLAYER_INFO>(body, PlayerController.GetPlayerInfo);
                    break;
                case Protocol.C_TO_U_OBJECT_INFO:
                    await HandleMessage<C_TO_U_OBJECT_INFO>(body, _updateObjectManager.GetObjectInfo);
                    break;
                case Protocol.C_TO_U_EXPLORE_TARGET_INFO:
                    await HandleMessage<C_TO_U_EXPLORE_TARGET_INFO>(body, JobController.GetExploreTargetInfo);
                    break;
                case Protocol.C_TO_U_JOB_RESOURCE_INFO:
                    await HandleMessage<C_TO_U_JOB_RESOURCE_INFO>(body, JobController.GetJobResourceInfo);
                    break;
                case Protocol.C_TO_U_UPGRADE_JOB:
                    await HandleMessage<C_TO_U_UPGRADE_JOB>(body, JobController.UpgradeJob);
                    break;
                case Protocol.C_TO_U_WEAR_ITEM:
                    await HandleMessage<C_TO_U_WEAR_ITEM>(body, InventoryController.RequestWearItem);
                    break;
                case Protocol.C_TO_U_USE_ITEM:
                    await HandleMessage<C_TO_U_USE_ITEM>(body, InventoryController.RequestUseItem);
                    break;
                case Protocol.C_TO_U_EXPLORE:
                    await HandleMessage<C_TO_U_EXPLORE>(body, JobController.Explore);
                    break;
                case Protocol.C_TO_U_USE_SKILL:
                    await HandleMessage<C_TO_U_USE_SKILL>(body, JobController.UseJobSkill);
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
                case Protocol.C_TO_U_MAKE:
                    await HandleMessage<C_TO_U_MAKE>(body, LabController.Make);
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
                    await HandleMessage<C_TO_U_ENCAMP>(body, JobController.Encamp);
                    break;
                case Protocol.C_TO_U_DECAMP:
                    await JobController.Decamp(this);
                    break;
                case Protocol.C_TO_U_ADD_SELL_ITEM:
                    await HandleMessage<C_TO_U_ADD_SELL_ITEM>(body, JobController.AddSellItem);
                    break;
                case Protocol.C_TO_U_DELETE_SELL_ITEM:
                    await HandleMessage<C_TO_U_DELETE_SELL_ITEM>(body, JobController.DeleteSellItem);
                    break;
                case Protocol.C_TO_U_BUY_ITEM:
                    await HandleMessage<C_TO_U_BUY_ITEM>(body, JobController.BuyItem);
                    break;
                case Protocol.C_TO_U_CAMP_INFO:
                    await HandleMessage<C_TO_U_CAMP_INFO>(body, CampController.GetCampInfo);
                    break;

                case Protocol.C_TO_U_SET_NAME:
                    await HandleMessage<C_TO_U_SET_NAME>(body, PlayerController.SetName);
                    break;

                case Protocol.C_TO_U_UPDATE_TUTORIAL:
                    await PlayerController.UpdateTutorial(this);
                    break;
            }
        }
        catch (Exception e)
        {
            _logManager.WriteErrorLog(e);
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
        _logManager.WriteInfoLog($"GameUser Removed. PlayerId:{PlayerId}");
        _onLeaveCallback(this);
    }

    public void SetState(PlayerInfo playerInfo, PlayerState state)
    {
        _playerManager.SetState(playerInfo, state);
    }

    public async Task SetFlip(DirectionType direction)
    {
        await _playerManager.SetFlip(direction);
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
        if (_playerManager.State != PlayerState.NONE)
            throw new Exception($"Already Initialized. {_playerManager.PlayerId}");

        var isDummy = false;
        if (!long.TryParse(request.AccountToken, out var tempPlayerId))
        {
            tempPlayerId = await CacheHelper.Instance.StringIncrementAsync("temp_player_id") + 1000;
            isDummy = true;
        }

        PlayerInfo? playerInfo;
        await using (await PlayerInfo.Lock(RedLock, tempPlayerId))
        {
            playerInfo = await PlayerInfo.Load(tempPlayerId);
            if (playerInfo == null)
            {
                playerInfo = new PlayerInfo(tempPlayerId, isDummy);

                // 기본템 지급
                var giftItemList = new List<ItemInfo>();
                foreach (var (itemId, count) in GameRuleData.DefaultItemList)
                {
                    var item = await InventoryController.CreateItem(itemId, count);
                    giftItemList.Add(item);
                }

                playerInfo.InventoryInfo.AddItem(giftItemList);
                // playerInfo.WearItem(giftItemList[0].ItemUid); // TODO 기본템 입히기
            }

            var movementHandler = new MovementHandler(_logManager, playerInfo.ObjectInfo, NatsClient, Send,
                _updateObjectManager, _playerManager.ChangeMap);
            var environmentHandler = new EnvironmentHandler(_logManager, _cts, RedLock, Send, playerInfo.ObjectInfo);

            await environmentHandler.StartAsync();

            _playerManager.Initialize(playerInfo.ObjectInfo, movementHandler, environmentHandler);

            using var duplicatePacket = Packet.Create((int)Protocol.U_TO_U_DUPLICATE);
            NatsClient.Publish(_playerManager.ObjectInfo.GetGameObjectKey(), duplicatePacket.ToBytes());

            await playerInfo.Save();
            await playerInfo.ObjectInfo.Save();
        }

        NatsClient.Subscribe(_playerManager.ObjectInfo.GetGameObjectKey(),
            (_, message) => OnMessageFromSubscribe(message));
        NatsClient.Subscribe("all", (_, message) => OnMessageFromSubscribe(message));

        var labInfo = await LabInfo.Load(playerInfo.LabId);

        using var loginPacket = PacketMaker.U_TO_C_LOGIN(playerInfo, labInfo ?? new LabInfo());
        Send(loginPacket);

        // 인벤토리 정보 전송
        await InventoryController.GetCurrentItemList(this);
        if (labInfo != null) await InventoryController.GetLabInventory(this);

        await _playerManager.ChangeMap();
    }

    public void BroadcastUpdateInfo<T>(T info) where T : IMessagePackObject
    {
        var objectInfo = info switch
        {
            PlayerInfo p => p.ObjectInfo,
            ExploreTargetInfo e => e.ObjectInfo,
            JobResourceInfo j => j.ObjectInfo,
            CampInfo c => c.ObjectInfo,
            _ => throw new ArgumentException($"Unsupported type: {typeof(T)}")
        };

        if (CommonMapData.IsCommonMap(objectInfo.MapId))
        {
            var partKey = CommonMapData.CreatePartKey(objectInfo.MapId, objectInfo.CurrentCell);
            var targetServerList = CommonMapData.GetBoundServerList(objectInfo.MapId, objectInfo.CurrentCell);
            foreach (var targetServer in targetServerList)
            {
                var subject = SubjectHelper.GetUpdateInfoSubject(objectInfo, targetServer);
                NatsClient.Publish(subject, MessagePackSerializer.Serialize((partKey, info)));
            }

            return;
        }

        var instancePartKey = InstanceMapData.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);
        var manageServer = InstanceMapData.GetManageServerId(objectInfo.MapSubId);
        var instanceSubject = SubjectHelper.GetUpdateInfoSubject(objectInfo, manageServer);
        NatsClient.Publish(instanceSubject, MessagePackSerializer.Serialize((instancePartKey, info)));
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

            await JobController.Decamp(this);
            await _playerManager.Dispose();
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
            _logManager.WriteErrorLog(ex);
        }
        finally
        {
            _token.LockDisconnect.Release();
        }

        return _token;
    }
}