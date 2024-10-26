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
        public readonly LogManager LogManager;
        private readonly UserToken _token;
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
            _token.SetPeer(this);

            RedLock = redLockFactory;
            NatsClient = natsClient;
            LogManager = logManager;
            _userLock = new(1);
            _cts = new();

            _progressManager = new(LogManager);
            _updateObjectChannel = Channel.CreateUnbounded<GameObjectInfo>(new() { SingleReader = false, SingleWriter = false });
            _updateObjectManager = new(_cts, LogManager, Send, _updateObjectChannel);
            _playerManager = new(LogManager, NatsClient, Send, _updateObjectManager);
            _onLeaveCallback = onLeaveCallback;

            LogManager.WriteInfoLog("Create GameUser Success!");
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
                            // HeartBeat();
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
                        await _playerManager.Spawn();
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
                LogManager.WriteErrorLog(e);
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
            Send(packet);
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
            using (await PlayerInfo.Lock(RedLock, tempPlayerId))
            {
                playerInfo = await PlayerInfo.Load(tempPlayerId);
                if (playerInfo == null)
                {
                    playerInfo = new(tempPlayerId, isDummy);

                    // 기본템 지급
                    var giftItemList = new List<ItemInfo>();
                    foreach (var (itemId, count) in Config.DEFAULT_ITEM_LIST)
                    {
                        var item = await InventoryController.CreateItem(itemId, count);
                        giftItemList.Add(item);
                    }

                    playerInfo.InventoryInfo.AddItem(giftItemList);
                    playerInfo.WearItem(giftItemList[0].ItemUid);
                    playerInfo.WearItem(giftItemList[1].ItemUid);
                }

                var movementHandler = new MovementHandler(LogManager, playerInfo.ObjectInfo, NatsClient, Send, _updateObjectManager, _playerManager.ChangeMap);
                var environmentHandler = new EnvironmentHandler(LogManager, _cts, RedLock, Send, playerInfo.ObjectInfo);

                await environmentHandler.StartAsync();

                _playerManager.Initialize(playerInfo.ObjectInfo, movementHandler, environmentHandler);

                using var duplicatePacket = Packet.Create((int)PROTOCOL.U_TO_U_DUPLICATE);
                NatsClient.Publish(_playerManager.ObjectInfo.GetGameObjectKey(), duplicatePacket.ToBytes());

                await playerInfo.Save();
                await playerInfo.ObjectInfo.Save();
            }

            NatsClient.Subscribe(_playerManager.ObjectInfo.GetGameObjectKey(), (channel, message) => OnMessageFromSubscribe(message));
            NatsClient.Subscribe("all", (channel, message) => OnMessageFromSubscribe(message));

            var labInfo = await LabInfo.Load(playerInfo.LabId);

            using var loginPacket = PacketMaker.U_TO_C_LOGIN(playerInfo, labInfo ?? new());
            Send(loginPacket);

            // 인벤토리 정보 전송
            var sendItemCount = await InventoryController.GetCurrentItemList(this);
            if (labInfo != null)
            {
                await InventoryController.GetLabInventory(this);
            }

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

            if (CommonMapHelper.IsCommonMap(objectInfo.MapId))
            {
                var partKey = CommonMapHelper.CreatePartKey(objectInfo.MapId, objectInfo.CurrentCell);
                var targetServerList = CommonMapHelper.GetBoundServerList(objectInfo.MapId, objectInfo.CurrentCell);
                foreach (var targetServer in targetServerList)
                {
                    var subject = SubjectHelper.GetUpdateInfoSubject(objectInfo, targetServer);
                    NatsClient.Publish(subject, MessagePackSerializer.Serialize((partKey, info)));
                }
                return;
            }

            var instancePartKey = InstanceMapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);
            var manageServer = InstanceMapHelper.GetManageServerId(objectInfo.MapSubId);
            var instanceSubject = SubjectHelper.GetUpdateInfoSubject(objectInfo, manageServer);
            NatsClient.Publish(instanceSubject, MessagePackSerializer.Serialize((instancePartKey, info)));
        }

        public void Send(IPacket msg)
        {
            if (msg is Packet packet)
            {
                _token.Send(packet);
                return;
            }

            throw new NotImplementedException();
        }

        public void PublishToClients(Packet packet, List<long> userIdList)
        {
            foreach (var userId in userIdList)
            {
                NatsClient.Publish(GameObjectInfo.MakeObjectKey(ObjectType.PLAYER, userId), packet.ToBytes());
            }
        }

        public static async Task SendToGameServer(Packet msg)
        {
            await CacheHelper.Instance.EnqueueAsync("game_server_queue", msg.ToBytes());
        }

        public void RecvDuplicate()
        {
            using var packet = Packet.Create((int)PROTOCOL.U_TO_U_DUPLICATE);
            Send(packet);
            OnRemoved();
        }

        public void OnRemoved()
        {
            LogManager.WriteInfoLog($"GameUser Removed. PlayerId:{PlayerId}");
            _onLeaveCallback(this);
        }

        public async Task<UserToken?> Release()
        {
            await _token.LockDisconnect.WaitAsync();
            try
            {
                if (_token.IsReleased)
                {
                    return null;
                }
                _token.IsReleased = true;
                _token.IsAlive = false;

                if (_playerManager != null)
                {
                    await JobController.Decamp(this);
                    await _playerManager.Dispose();
                    _progressManager.Dispose();
                    _updateObjectManager.Dispose();
                }

                using var packet = PacketMaker.U_TO_G_LOGOUT(PlayerId);
                await SendToGameServer(packet);

                _cts.Cancel();
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
}
