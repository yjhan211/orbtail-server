using System.Threading.Channels;
using RedLockNet.SERedis;
using MessagePack;
using network.core;
using network.common;
using network.utils;
using network.infrastructure;
using network.interfaces;
using network.helpers;
using network.managers;
using network.packets;
using user_server.managers;
using user_server.controllers;
using user_server.handlers;

namespace user_server
{
    public partial class GameUser : IPeer
    {
        private readonly UserToken _token;
        private readonly LogManager _logManager;
        private readonly SemaphoreSlim _userLock;
        private readonly CancellationTokenSource _cts;
        public readonly RedLockFactory RedLock;
        public readonly NatsClient NatsClient;
        private readonly Channel<GameObjectInfo> _updateObjectChannel;
        private readonly ProgressManager _progressManager;
        private readonly UpdateObjectManager _updateObjectManager;
        private readonly PlayerManager _playerManager;
        private readonly Action<GameUser> _onLeaveCallback;

        public long PlayerId => _playerManager.PlayerId;
        public PlayerState PlayerState => _playerManager.State;
        public Cell CurrentCell => _playerManager.CurrentCell;

        public void SetState(PlayerInfo playerInfo, PlayerState state) => _playerManager.SetState(playerInfo, state);
        public async Task SetFlip(DirectionType direction) => await _playerManager.SetFlip(direction);

        public GameUser(UserToken token, RedLockFactory redLockFactory, NatsClient natsClient, LogManager logManager, Action<GameUser> onLeaveCallback)
        {
            _token = token;
            RedLock = redLockFactory;
            NatsClient = natsClient;
            _logManager = logManager;
            _userLock = new(1);
            _cts = new();

            _progressManager = new(_logManager);
            _updateObjectChannel = Channel.CreateUnbounded<GameObjectInfo>(new() { SingleReader = false, SingleWriter = false });
            _updateObjectManager = new(_cts, _logManager, SendToClient, _updateObjectChannel);

            _playerManager = new(NatsClient, SendToClient, _updateObjectManager);
            _onLeaveCallback = onLeaveCallback;
        }

        private void HandleMessage<T>(byte[] body, Func<GameUser, T, Task> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            handleMessage(this, msg);
        }

        private void HandleMessage<T>(byte[] body, Action<GameUser, T> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            handleMessage(this, msg);
        }

        private static readonly IReadOnlyList<PROTOCOL> NonAuthProtocol = new List<PROTOCOL>
        {
            PROTOCOL.C_TO_U_HEART_BEAT,
            PROTOCOL.C_TO_U_LOGIN
        };

        private static readonly IReadOnlyList<PROTOCOL> ActionProtocol = new List<PROTOCOL>
        {
            PROTOCOL.C_TO_U_MOVE,
            PROTOCOL.C_TO_U_WEAR_ITEM,
            PROTOCOL.C_TO_U_USE_SKILL
        };

        public async Task OnMessageFromClient(Const<byte[]> buffer)
        {
            try
            {
                await _userLock.WaitAsync();

                using var packet = Packet.Create(buffer);
                var protocolId = (PROTOCOL)packet.PopProtocolId();
                _ = packet.PopPlayerId();
                var body = packet.PopBody();

                if (NonAuthProtocol.Contains(protocolId))
                {
                    switch (protocolId)
                    {
                        case PROTOCOL.C_TO_U_HEART_BEAT:
                            HeartBeat();
                            break;
                        case PROTOCOL.C_TO_U_LOGIN:
                            HandleMessage<C_TO_U_LOGIN>(body, Login);
                            break;
                    }
                    return;
                }

                if (_playerManager.State == PlayerState.NONE)
                {
                    throw new Exception($"invalid PlayerState");
                }

                if (ActionProtocol.Contains(protocolId) && _playerManager.State != PlayerState.IDLE)
                {
                    throw new Exception($"in action. {_playerManager.PlayerId}");
                }

                switch (protocolId)
                {
                    case PROTOCOL.C_TO_U_CHANGE_MAP_SUCCESS:
                        await _playerManager.SpawnSuccess();
                        break;
                    case PROTOCOL.C_TO_U_CHAT_LOG:
                        await ChatController.GetChatHistory(this, ChatType.ALL);
                        break;
                    case PROTOCOL.C_TO_U_MOVE:
                        HandleMessage<C_TO_U_MOVE>(body, _playerManager.RequestMove);
                        break;
                    case PROTOCOL.C_TO_U_PLAYER_INFO:
                        HandleMessage<C_TO_U_PLAYER_INFO>(body, PlayerController.GetPlayerInfo);
                        break;
                    case PROTOCOL.C_TO_U_OBJECT_INFO:
                        HandleMessage<C_TO_U_OBJECT_INFO>(body, _updateObjectManager.GetObjectInfo);
                        break;
                    case PROTOCOL.C_TO_U_EXPLORE_TARGET_INFO:
                        HandleMessage<C_TO_U_EXPLORE_TARGET_INFO>(body, JobController.GetExploreTargetInfo);
                        break;
                    case PROTOCOL.C_TO_U_JOB_RESOURCE_INFO:
                        HandleMessage<C_TO_U_JOB_RESOURCE_INFO>(body, JobController.GetJobResourceInfo);
                        break;
                    case PROTOCOL.C_TO_U_UPGRADE_JOB:
                        HandleMessage<C_TO_U_UPGRADE_JOB>(body, JobController.UpgradeJob);
                        break;
                    case PROTOCOL.C_TO_U_WEAR_ITEM:
                        HandleMessage<C_TO_U_WEAR_ITEM>(body, InventoryController.RequestWearItem);
                        break;
                    case PROTOCOL.C_TO_U_USE_ITEM:
                        HandleMessage<C_TO_U_USE_ITEM>(body, InventoryController.RequestUseItem);
                        break;
                    case PROTOCOL.C_TO_U_EXPLORE:
                        HandleMessage<C_TO_U_EXPLORE>(body, JobController.Explore);
                        break;
                    case PROTOCOL.C_TO_U_USE_SKILL:
                        HandleMessage<C_TO_U_USE_SKILL>(body, JobController.UseJobSkill);
                        break;
                    case PROTOCOL.C_TO_U_CHAT_MSG:
                        HandleMessage<C_TO_U_CHAT_MSG>(body, ChatController.SendChat);
                        break;
                    case PROTOCOL.C_TO_U_CREATE_LAB:
                        HandleMessage<C_TO_U_CREATE_LAB>(body, LabController.CreateLab);
                        break;
                    case PROTOCOL.C_TO_U_UPGRADE_RESEARCH:
                        HandleMessage<C_TO_U_UPGRADE_RESEARCH>(body, LabController.UpgradeResearch);
                        break;
                    case PROTOCOL.C_TO_U_MAKE:
                        HandleMessage<C_TO_U_MAKE>(body, LabController.Make);
                        break;
                    case PROTOCOL.C_TO_U_WRITE_LAB_HIRE:
                        HandleMessage<C_TO_U_WRITE_LAB_HIRE>(body, LabController.WriteLabHire);
                        break;
                    case PROTOCOL.C_TO_U_LAB_HIRE_LIST:
                        await LabController.LabHireList(this);
                        break;
                    case PROTOCOL.C_TO_U_JOIN_LAB:
                        HandleMessage<C_TO_U_JOIN_LAB>(body, LabController.JoinLab);
                        break;
                    case PROTOCOL.C_TO_U_LAB_INVENTORY:
                        await InventoryController.GetLabInventory(this);
                        break;
                    case PROTOCOL.C_TO_U_LAB_INVENTORY_ADD_ITEM:
                        HandleMessage<C_TO_U_LAB_INVENTORY_ADD_ITEM>(body, InventoryController.AddLabItem);
                        break;
                    case PROTOCOL.C_TO_U_LAB_INVENTORY_TAKE_ITEM:
                        HandleMessage<C_TO_U_LAB_INVENTORY_TAKE_ITEM>(body, InventoryController.TakeLabItem);
                        break;
                    case PROTOCOL.C_TO_U_ENCAMP:
                        HandleMessage<C_TO_U_ENCAMP>(body, JobController.Encamp);
                        break;
                    case PROTOCOL.C_TO_U_DECAMP:
                        await JobController.Decamp(this);
                        break;
                    case PROTOCOL.C_TO_U_ADD_SELL_ITEM:
                        HandleMessage<C_TO_U_ADD_SELL_ITEM>(body, JobController.AddSellItem);
                        break;
                    case PROTOCOL.C_TO_U_DELETE_SELL_ITEM:
                        HandleMessage<C_TO_U_DELETE_SELL_ITEM>(body, JobController.DeleteSellItem);
                        break;
                    case PROTOCOL.C_TO_U_BUY_ITEM:
                        HandleMessage<C_TO_U_BUY_ITEM>(body, JobController.BuyItem);
                        break;
                    case PROTOCOL.C_TO_U_CAMP_INFO:
                        HandleMessage<C_TO_U_CAMP_INFO>(body, CampController.GetCampInfo);
                        break;

                    case PROTOCOL.C_TO_U_SET_NAME:
                        HandleMessage<C_TO_U_SET_NAME>(body, PlayerController.SetName);
                        break;

                    case PROTOCOL.C_TO_U_UPDATE_TUTORIAL:
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

        private void HeartBeat()
        {
            _token.IsAlive = true;

            using var packet = PacketMaker.U_TO_C_HEART_BEAT(DateTime.UtcNow);
            SendToClient(packet);
        }

        private async Task Login(GameUser _, C_TO_U_LOGIN request)
        {
            if (_playerManager.State != PlayerState.NONE)
            {
                throw new Exception($"Already Initialized. {_playerManager.PlayerId}");
            }

            bool isDummy = false;
            if (!long.TryParse(request.AccountToken, out long tempPlayerId))
            {
                tempPlayerId = await CacheHelper.Instance.StringIncrementAsync("temp_player_id") + 1000;
                isDummy = true;
            }

            PlayerInfo? playerInfo = null;
            using (await PlayerInfo.Lock(RedLock, _playerManager.PlayerId))
            {
                playerInfo = await PlayerInfo.Load(tempPlayerId);
                if (playerInfo == null)
                {
                    playerInfo = new(tempPlayerId, isDummy);

                    // 기본템 지급
                    List<ItemInfo> giftItemList = new();
                    foreach (var (itemId, count) in Config.DEFAULT_ITEM_LIST)
                    {
                        var item = await InventoryController.CreateItem(itemId, count);
                        giftItemList.Add(item);
                    }

                    playerInfo.InventoryInfo.AddItem(giftItemList);
                    playerInfo.WearItem(giftItemList[0].ItemUid);
                    playerInfo.WearItem(giftItemList[1].ItemUid);
                }

                var movementHandler = new MovementHandler(playerInfo.ObjectInfo, NatsClient, _updateObjectManager, _playerManager.Spawn);
                var environmentHandler = new EnvironmentHandler(_logManager, _cts, RedLock, SendToClient, playerInfo.ObjectInfo);
                await _playerManager.Initialize(playerInfo.ObjectInfo, movementHandler, environmentHandler);

                using var duplicatePacket = Packet.Create((int)PROTOCOL.U_TO_U_DUPLICATE);
                NatsClient.Publish(_playerManager.ObjectInfo.GetHashField(), duplicatePacket.ToBytes());

                await playerInfo.Save();
                await playerInfo.ObjectInfo.Save();
            }

            NatsClient.Subscribe(playerInfo.ObjectInfo.GetHashField(), (channel, message) => OnMessageFromSubscribe(message));
            NatsClient.Subscribe("all", (channel, message) => OnMessageFromSubscribe(message));

            var labInfo = await LabInfo.Load(playerInfo.LabId);

            using var loginPacket = PacketMaker.U_TO_C_LOGIN(playerInfo, labInfo ?? new());
            SendToClient(loginPacket);

            // 인벤토리 정보 전송
            await InventoryController.GetCurrentItemList(this);
            if (labInfo != null)
            {
                await InventoryController.GetLabInventory(this);
            }

            await _playerManager.Spawn();
        }

        public void BroadcastUpdatePlayerInfo(PlayerInfo playerInfo)
        {
            switch (playerInfo.ObjectInfo.MapId)
            {
                case MapID.LAB_1:
                    var instanceKey = MapHelper.GetInstanceKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId);
                    var instanceServer = MapHelper.GetServerIdByMapSubID(Program.GameServerNum, playerInfo.ObjectInfo.MapSubId);
                    var labSubject = MapHelper.GetUpdatePlayerSubject(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId, instanceServer);
                    NatsClient.Publish(labSubject, MessagePackSerializer.Serialize((instanceKey, playerInfo)));
                    break;

                default:
                    var position_key = MapHelper.GetPositionKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId, playerInfo.ObjectInfo.CurrentCell);
                    var targetServerList = MapHelper.GetBoundServerList(playerInfo.ObjectInfo.MapId, Program.GameServerNum, MapHelper.GetCell(position_key));
                    foreach (var targetServer in targetServerList)
                    {
                        var subject = MapHelper.GetUpdatePlayerSubject(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId, targetServer);
                        NatsClient.Publish(subject, MessagePackSerializer.Serialize((position_key, playerInfo)));
                    }
                    break;
            }
        }

        public void BroadcastUpdateExploreTargetInfo(ExploreTargetInfo exploreTargetInfo)
        {
            var positionKey = MapHelper.GetPositionKey(exploreTargetInfo.ObjectInfo.MapId, exploreTargetInfo.ObjectInfo.MapSubId, exploreTargetInfo.ObjectInfo.CurrentCell);
            var targetServerList = MapHelper.GetBoundServerList(exploreTargetInfo.ObjectInfo.MapId, Program.GameServerNum, MapHelper.GetCell(positionKey));
            foreach (var targetServer in targetServerList)
            {
                var subject = MapHelper.GetUpdateExploreTargetSubject(exploreTargetInfo.ObjectInfo.MapId, exploreTargetInfo.ObjectInfo.MapSubId, targetServer);
                NatsClient.Publish(subject, MessagePackSerializer.Serialize((positionKey, exploreTargetInfo)));
            }
        }

        public void BroadcastUpdateJobResourceInfo(JobResourceInfo jobResourceInfo)
        {
            var positionKey = MapHelper.GetPositionKey(jobResourceInfo.ObjectInfo.MapId, jobResourceInfo.ObjectInfo.MapSubId, jobResourceInfo.ObjectInfo.CurrentCell);
            var targetServerList = MapHelper.GetBoundServerList(jobResourceInfo.ObjectInfo.MapId, Program.GameServerNum, MapHelper.GetCell(positionKey));
            foreach (var targetServer in targetServerList)
            {
                var subject = MapHelper.GetUpdateJobResourceSubject(jobResourceInfo.ObjectInfo.MapId, jobResourceInfo.ObjectInfo.MapSubId, targetServer);
                NatsClient.Publish(subject, MessagePackSerializer.Serialize((positionKey, jobResourceInfo)));
            }
        }

        public void SendToClient(IPacket msg)
        {
            if (msg is Packet packet)
            {
                _token.Send(packet);
            }
            throw new NotImplementedException();
        }

        public void PublishToClients(Packet packet, List<long> userIdList)
        {
            foreach (var userId in userIdList)
            {
                NatsClient.Publish(GameObjectInfo.MakeHashField(ObjectType.PLAYER, userId), packet.ToBytes());
            }
        }

        public static async Task SendToGameServer(Packet msg)
        {
            await CacheHelper.Instance.EnqueueAsync("game_server_queue", msg.ToBytes());
        }

        public void RecvDuplicate()
        {
            using var packet = Packet.Create((int)PROTOCOL.U_TO_U_DUPLICATE);
            SendToClient(packet);
            OnRemoved();
        }

        public void OnRemoved()
        {
            _cts.Cancel();
            _cts.Dispose();

            _onLeaveCallback(this);
        }

        public async Task<UserToken> Release()
        {
            await JobController.Decamp(this);

            if (_playerManager != null)
            {
                await _playerManager.Dispose();
                _progressManager.Dispose();
                _updateObjectManager.Dispose();
            }

            using var packet = PacketMaker.U_TO_G_LOGOUT(PlayerId);
            await SendToGameServer(packet);

            NatsClient.Close();

            return _token;
        }
    }
}
