using System.Diagnostics.CodeAnalysis;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.helpers;
using network.interfaces;
using network.packets;
using network.utils;
using StackExchange.Redis;
using user_server.controllers;
using user_server.players;

namespace user_server;

public class GameUser : IPeer
{
    private const string GlobalSubscribeChannel = "all";
    private const string TempPlayerIdKey = "temp_player_id";
    private const string GameServerQueue = "game_server_queue";

    private readonly UserToken _token;
    private readonly SemaphoreSlim _userLock;
    private readonly Action<GameUser> _onLeaveCallback;
    private Dictionary<Protocol, Func<byte[], Task>> _protocolHandlers;
    private Dictionary<Protocol, Func<byte[], Task>> _subscribeHandlers;
    
    private readonly ChatController _chatController;
    private PlayerController? _playerController;
    
    public readonly CancellationTokenSource Cts;

    public readonly MapObjectController MapObjectController;
    public readonly ICacheHelper CacheHelper;
    public readonly INatsClient NatsClient;
    public readonly IRedLockFactory RedLock;
    public readonly ILogger Logger;

    private static readonly IReadOnlyList<Protocol> NonAuthProtocol = new List<Protocol>
    {
        Protocol.C_TO_U_HEART_BEAT,
        Protocol.C_TO_U_LOGIN
    };


    public GameUser(UserToken token, IRedLockFactory redLock, INatsClient natsClient, ILogger logger, ICacheHelper cacheHelper, Action<GameUser> onLeaveCallback, ChatController chatController)
    {
        _token = token;
        _token.SetPeer(this);
        _userLock = new SemaphoreSlim(1);
        Cts = new CancellationTokenSource();
        
        CacheHelper = cacheHelper;
        RedLock = redLock;
        NatsClient = natsClient;
        Logger = logger;
        
        _onLeaveCallback = onLeaveCallback;
        _chatController = chatController;
        MapObjectController = new MapObjectController(this);
        
        InitializeProtocolHandlers();
        InitializeSubscribeHandlers();

        Logger.LogInformation("Create GameUser Success!");
    }

    [MemberNotNull(nameof(_protocolHandlers))]
    private void InitializeProtocolHandlers()
    {
        _protocolHandlers = new Dictionary<Protocol, Func<byte[], Task>>
        {
           { Protocol.C_TO_U_HEART_BEAT, HandleHeartBeat },
           { Protocol.C_TO_U_LOGIN, async (bytes) => await HandleMessage<C_TO_U_LOGIN>(bytes, Login) },
           { Protocol.C_TO_U_CHANGE_MAP_SUCCESS, async (_) => await HandlePlayerAction(pc => pc.Spawn()) },
           { Protocol.C_TO_U_CHAT_LOG, async (_) => await SendChatHistory(ChatType.ALL) },
           { Protocol.C_TO_U_MOVE, async (bytes) => await HandleMessage<C_TO_U_MOVE>(bytes, msg => HandlePlayerAction(pc => pc.RequestMove(msg))) },
           { Protocol.C_TO_U_PLAYER_INFO, async (bytes) => await HandleMessage<C_TO_U_PLAYER_INFO>(bytes, GetPlayerInfo) },
           { Protocol.C_TO_U_EXPLORE_TARGET_INFO, async (bytes) => await HandleMessage<C_TO_U_EXPLORE_TARGET_INFO>(bytes, GetExploreTargetInfo) },
           { Protocol.C_TO_U_WEAR_ITEM, async (bytes) => await HandleMessage<C_TO_U_WEAR_ITEM>(bytes, msg => HandlePlayerAction(pc => pc.Wear(msg))) },
           { Protocol.C_TO_U_USE_ITEM, async (bytes) => await HandleMessage<C_TO_U_USE_ITEM>(bytes, msg => HandlePlayerAction(pc => pc.Use(msg))) },
           { Protocol.C_TO_U_CHANGE_MAP, async (bytes) => await HandleMessage<C_TO_U_CHANGE_MAP>(bytes, msg => HandlePlayerAction(pc => pc.ChangeMap(msg))) },
           { Protocol.C_TO_U_EXPLORE, async (bytes) => await HandleMessage<C_TO_U_EXPLORE>(bytes, msg => HandlePlayerAction(pc => pc.Explore(msg))) },
           { Protocol.C_TO_U_CHAT_MSG, async (bytes) => await HandleMessage<C_TO_U_CHAT_MSG>(bytes, AppendChat) },
           { Protocol.C_TO_U_CRAFT, async (bytes) => await HandleMessage<C_TO_U_CRAFT>(bytes, msg => HandlePlayerAction(pc => pc.Craft(msg))) },
           { Protocol.C_TO_U_ENCAMP, async (bytes) => await HandleMessage<C_TO_U_ENCAMP>(bytes, msg => HandlePlayerAction(pc => pc.Encamp(msg))) },
           { Protocol.C_TO_U_DECAMP, async (_) => await HandlePlayerAction(pc => pc.Decamp()) }, { Protocol.C_TO_U_CAMP_INFO, async (bytes) => await HandleMessage<C_TO_U_CAMP_INFO>(bytes, GetCampInfo) },
           { Protocol.C_TO_U_SET_NAME, async (bytes) => await HandleMessage<C_TO_U_SET_NAME>(bytes, msg => HandlePlayerAction(pc => pc.SetName(msg))) },
           { Protocol.C_TO_U_BOOST, async (bytes) => await HandleMessage<C_TO_U_BOOST>(bytes, msg => HandlePlayerAction(pc => pc.UpdateBoost(msg))) },
           { Protocol.C_TO_U_SOCIAL_ACTION, async (bytes) => await HandleMessage<C_TO_U_SOCIAL_ACTION>(bytes, msg => HandlePlayerAction(pc => pc.SocialAction(msg))) },
           { Protocol.C_TO_U_QUEST_INCREASE, async (bytes) => await HandleMessage<C_TO_U_QUEST_INCREASE>(bytes, msg => HandlePlayerAction(pc => pc.IncreaseQuestCount(msg))) },
           { Protocol.C_TO_U_QUEST_SUCCESS, async (bytes) => await HandleMessage<C_TO_U_QUEST_SUCCESS>(bytes, msg => HandlePlayerAction(pc => pc.CompleteQuest(msg))) },
           { Protocol.C_TO_U_MAIL_LIST, async (_) => await HandlePlayerAction(pc => pc.SendCurrentMails()) }, { Protocol.C_TO_U_MAIL_RECEIVE, async (bytes) => await HandleMessage<C_TO_U_MAIL_RECEIVE>(bytes, msg => HandlePlayerAction(pc => pc.ReceiveMail(msg))) },
           { Protocol.C_TO_U_ITEM_PUT, async (bytes) => await HandleMessage<C_TO_U_ITEM_PUT>(bytes, msg => HandlePlayerAction(pc => pc.PutItem(msg))) },
           { Protocol.C_TO_U_OBJECT_INFO, async (bytes) => await HandleMessage<C_TO_U_OBJECT_INFO>(bytes, MapObjectController.GetObjectInfo) },
        };
    }

    [MemberNotNull(nameof(_subscribeHandlers))]
    private void InitializeSubscribeHandlers()
    {
        _subscribeHandlers = new Dictionary<Protocol, Func<byte[], Task>>
        {
           { Protocol.G_TO_U_UPDATE_OBJECT, bytes => HandleMessage<G_TO_U_UPDATE_OBJECT>(bytes, SubscribeUpdateObject) },
           { Protocol.G_TO_U_SPAWN, bytes => HandleMessage<G_TO_U_SPAWN>(bytes, SubscribeSpawn) },
           { Protocol.G_TO_U_DESTROY, bytes => HandleMessage<G_TO_U_DESTROY>(bytes, SubscribeDestroy) },
           { Protocol.U_TO_C_CHAT_MSG, bytes => HandleMessage<U_TO_C_CHAT_MSG>(bytes, SubscribeChatMsg) },
           { Protocol.G_TO_U_PLAYER_INFO, bytes => HandleMessage<G_TO_U_PLAYER_INFO>(bytes, SubscribePlayerInfo) },
           { Protocol.G_TO_U_EXPLORE_TARGET_INFO, bytes => HandleMessage<G_TO_U_EXPLORE_TARGET_INFO>(bytes, SubscribeExploreTargetInfo) },
           { Protocol.G_TO_U_ENTER_INSTANCE_SUCCESS, bytes => HandleMessage<G_TO_U_ENTER_INSTANCE_SUCCESS>(bytes, SubscribeEnterInstanceSuccess) },
           { Protocol.G_TO_U_CAMP_INFO, bytes => HandleMessage<G_TO_U_CAMP_INFO>(bytes, SubscribeCampInfo) },
           { Protocol.G_TO_U_SOCIAL_ACTION, bytes => HandleMessage<G_TO_U_SOCIAL_ACTION>(bytes, SubscribeSocialAction) },
           { Protocol.U_TO_U_DUPLICATE, _ => { ReceiveDuplicate(); return Task.CompletedTask; }}
        };
    }

    public async Task OnMessageFromClient(Const<byte[]> buffer)
    {
        try
        {
            await _userLock.WaitAsync();

            using var packet = Packet.Create(buffer);
            var protocolId = (Protocol)packet.PopProtocolId();
            var playerId = packet.PopPlayerId();
            var body = packet.PopBody();

            Logger.LogInformation("[{PlayerId}] {ProtocolId}", playerId, protocolId);

            if (!_protocolHandlers.TryGetValue(protocolId, out var handler))
            {
                throw new NotSupportedException($"[{playerId}] Unsupported protocol: {protocolId}");
            }

            if (NonAuthProtocol.Contains(protocolId))
            {
                await handler(body);
                return;
            }

            if (_playerController == null)
            {
                throw new Exception($"[{playerId}] not login");
            }
            
            if (_playerController.IsInvalidAction(protocolId))
            {
                throw new Exception($"[{playerId}] in action. protocolId: {protocolId}");
            }

            await handler(body);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "onMessageFromClient");
        }
        finally
        {
            _userLock.Release();
        }
    }

    private async Task OnMessageFromSubscribe(byte[] message)
    {
        try
        {
            await _userLock.WaitAsync();

            using var packet = new Packet(message);
            var protocolId = (Protocol)packet.PopProtocolId();
            _ = packet.PopPlayerId();
            var body = packet.PopBody();

            if (!_subscribeHandlers.TryGetValue(protocolId, out var handler))
            {
                throw new NotSupportedException($"Unsupported subscribe protocol: {protocolId}");
            }

            await handler(body);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "onMessageFromSubscribe");
        }
        finally
        {
            _userLock.Release();
        }
    }

    private void SubscribeHandler(string subject, Func<byte[], Task> handler)
    {
        NatsClient.Subscribe(subject, (_, message) =>
        {
            var unused = Task.Run(async () =>
            {
                try
                {
                    await handler(message);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "onMessageFromSubscribe");
                }
            });
        });
    }

    private async Task HandleMessage<T>(byte[] body, Func<T, Task> handleMessage)
    {
        var msg = MessagePackSerializer.Deserialize<T>(body);
        await handleMessage(msg);
    }

    private async Task HandlePlayerAction(Func<PlayerController, Task> action)
    {
        if (_playerController == null)
        {
            throw new InvalidOperationException("Player controller is not initialized");
        }
            
        await action(_playerController);
    }

    private Task HandleHeartBeat(byte[] _)
    {
        using var packet = PacketMaker.U_TO_C_HEART_BEAT(DateTime.UtcNow);
        Send(packet);
        return Task.CompletedTask;
    }
    
    private async Task Login(C_TO_U_LOGIN request)
    {
        if (_playerController != null)
        {
            throw new Exception($"Already Initialized. {request.AccountToken}");
        }

        var isDummy = false;
        if (!long.TryParse(request.AccountToken, out var tempPlayerId))
        {
            tempPlayerId = await CacheHelper.StringIncrementAsync(TempPlayerIdKey) + 1000;
            isDummy = true;
        }

        PlayerInfo playerInfo;
        await using (await PlayerInfo.Lock(RedLock, tempPlayerId))
        {
            playerInfo = await PlayerInfo.Load(CacheHelper, tempPlayerId) ?? await Register(tempPlayerId, isDummy);
            _playerController = new PlayerController(this, playerInfo);
            if (playerInfo.IsNew)
            {
                // 기본템 입히기
                var defaultTop = playerInfo.InventoryInfo.ItemDict.First(x => x.Value.ItemId == 104000001);
                var defaultBottom = playerInfo.InventoryInfo.ItemDict.First(x => x.Value.ItemId == 105000001);
                var defaultShoes = playerInfo.InventoryInfo.ItemDict.First(x => x.Value.ItemId == 106000001);

                await _playerController.Wear(new C_TO_U_WEAR_ITEM(defaultTop.Value.ItemUid));
                await _playerController.Wear(new C_TO_U_WEAR_ITEM(defaultBottom.Value.ItemUid));
                await _playerController.Wear(new C_TO_U_WEAR_ITEM(defaultShoes.Value.ItemUid));
            }
            
            using var duplicatePacket = Packet.Create((int)Protocol.U_TO_U_DUPLICATE);
            NatsClient.Publish(playerInfo.ObjectInfo.GetGameObjectKey(), duplicatePacket.ToBytes());
            
            await playerInfo.Save(CacheHelper);
            await playerInfo.ObjectInfo.Save(CacheHelper);
        }
        
        SubscribeHandler(playerInfo.ObjectInfo.GetGameObjectKey(), OnMessageFromSubscribe);
        SubscribeHandler(GlobalSubscribeChannel, OnMessageFromSubscribe);

        var labInfo = await LabInfo.Load(CacheHelper, playerInfo.LabId);

        using var loginPacket = PacketMaker.U_TO_C_LOGIN(playerInfo, labInfo ?? new LabInfo());
        Send(loginPacket);

        var mailInfo = await MailBox.Load(CacheHelper, tempPlayerId);
        if (mailInfo.MailDict.Count <= 0)
        {
            var firstMail = await PlayerMailBox.CreateMail(CacheHelper, 1);
            await _playerController.SendMail(firstMail);
        }

        var questInfo = await QuestDiary.Load(CacheHelper, tempPlayerId);
        if (questInfo.QuestDict.Count <= 0)
        {
            await _playerController.StartQuest(100000001);
        }

        _playerController.SendCurrentItems();
        await _playerController.SendCurrentMails();
        await _playerController.SendCurrentQuests();
        await _playerController.EnterMap(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.CurrentCell, playerInfo.ObjectInfo.IsFlip, true);
    }

    private async Task<PlayerInfo> Register(long playerId, bool isDummy = false)
    {
        var playerInfo = new PlayerInfo(playerId, isDummy);
        foreach (var (itemId, count) in GameRuleData.DefaultItemList)
        {
            var item = await PlayerInventory.CreateItem(CacheHelper, itemId, count);
            playerInfo.InventoryInfo.ItemDict.Add(item.ItemUid, item);
        }
        
        return playerInfo;
    }

    private async Task GetPlayerInfo(C_TO_U_PLAYER_INFO body)
    {
        var keys = body.PlayerIdList.ConvertAll(x => (RedisValue)x).ToArray();
        var playerInfoList = await PlayerInfo.LoadAll(CacheHelper, keys);

        using var packet = PacketMaker.U_TO_C_PLAYER_INFO(playerInfoList);
        Send(packet);
    }
    
    private async Task GetExploreTargetInfo(C_TO_U_EXPLORE_TARGET_INFO body)
    {
        var targetInfoList = new List<ExploreTargetInfo>();
        for (var i = 0; i < body.ExploreTargetIdList.Count; i++)
        {
            var targetExploreUid = body.ExploreTargetIdList[i];
            ExploreTargetInfo? targetExploreInfo;

            await using (await ExploreTargetInfo.Lock(RedLock, targetExploreUid))
            {
                targetExploreInfo = await ExploreTargetInfo.Load(CacheHelper, targetExploreUid);
            }

            if (targetExploreInfo == null)
            {
                continue;
            }

            targetInfoList.Add(targetExploreInfo);

            var isMax = targetInfoList.Count >= Config.BROADCAST_UNIT;
            var isLast = i == body.ExploreTargetIdList.Count - 1;

            if (!isMax && !isLast)
            {
                continue;
            }

            using var packet = PacketMaker.U_TO_C_EXPLORE_TARGET_INFO(targetInfoList);
            Send(packet);
            targetInfoList.Clear();
        }
    }
    
    private async Task GetCampInfo(C_TO_U_CAMP_INFO body)
    {
        var campIdList = body.CampInfoList;
        var campInfoList = new List<CampInfo>();

        for (var i = 0; i < campIdList.Count; i++)
        {
            var targetCampInfo = await CampInfo.Load(CacheHelper, campIdList[i]);
            if (targetCampInfo == null)
            {
                continue;
            }

            campInfoList.Add(targetCampInfo);

            var isMax = campInfoList.Count >= Config.BROADCAST_UNIT;
            var isEnded = i == campInfoList.Count - 1;
            if (!isMax && !isEnded)
            {
                continue;
            }

            using var packet = PacketMaker.U_TO_C_CAMP_INFO(campInfoList);
            Send(packet);
        }
    }

    private async Task SendChatHistory(ChatType chatType)
    {
        var result = await _chatController.GetChatHistory(chatType);
        foreach (var item in result)
        {
            using var packet = PacketMaker.U_TO_C_CHAT_MSG(item.Item1, item.Item2, item.Item3, item.Item4);
            Send(packet);
        }
    }

    private async Task AppendChat(C_TO_U_CHAT_MSG body)
    {
        if (body.ChatMessage.Length >= Config.MAX_CHAT_LENGTH)
        {
            return;
        }

        if (_playerController == null)
        {
            return;
        }

        await _chatController.SendChat(_playerController.PlayerId, _playerController.PlayerName, body.ChatType, body.ChatMessage, NatsClient);
    }

    private void ReceiveDuplicate()
    {
        using var packet = Packet.Create((int)Protocol.U_TO_U_DUPLICATE);
        Send(packet);
        OnRemoved();
    }

    private Task SubscribeUpdateObject(G_TO_U_UPDATE_OBJECT body)
    {
        if (body.ObjectInfo.ObjectType == ObjectType.PLAYER && _playerController != null && body.ObjectInfo.ObjectId == _playerController.PlayerId)
        {
            return Task.CompletedTask;
        }

        MapObjectController.EnqueueUpdateObject(body.ObjectInfo);
        return Task.CompletedTask;
    }

    private Task SubscribeSpawn(G_TO_U_SPAWN body)
    {
        if (_playerController == null)
        {
            return Task.CompletedTask;
        }
   
        var objectKeys = body.ObjectKeyList.Where(key => key != _playerController.ObjectKey).ToList();
        var cellsToRemove = body.CellsToRemove;

        var batchCount = (int)Math.Ceiling((double)Math.Max(objectKeys.Count, cellsToRemove.Count) / Config.BROADCAST_UNIT);
        for (var i = 0; i < batchCount; i++)
        {
            SendSpawnBatch(objectKeys, cellsToRemove, Config.BROADCAST_UNIT, i, batchCount);
        }
       
        return Task.CompletedTask;
    }

    private void SendSpawnBatch(List<string> objects, List<Cell> cells, int batchSize, int batchIndex, int totalBatches)
    {
        var batch = objects
            .Skip(batchIndex * batchSize)
            .Take(batchSize)
            .Select(item => item.ToString())
            .ToList();
           
        var cellBatch = cells
            .Skip(batchIndex * batchSize)
            .Take(batchSize)
            .ToList();

        var isEnded = batchIndex == totalBatches - 1;
       
        using var packet = PacketMaker.U_TO_C_SPAWN(batch, isEnded, cellBatch);
        Send(packet);
    }

    private Task SubscribeDestroy(G_TO_U_DESTROY body)
    {
        using var packet = PacketMaker.U_TO_C_DESTROY(body.ObjectKey);
        Send(packet);
       
        return Task.CompletedTask;
    }

    private Task SubscribeChatMsg(U_TO_C_CHAT_MSG body)
    {
        using var packet = PacketMaker.U_TO_C_CHAT_MSG(body.ChatType, body.PlayerId, body.Name, body.ChatMessage);
        Send(packet);
       
        return Task.CompletedTask;
    }

    private Task SubscribePlayerInfo(G_TO_U_PLAYER_INFO body)
    {
        using var packet = PacketMaker.U_TO_C_PLAYER_INFO([body.PlayerInfo]);
        Send(packet);
       
        return Task.CompletedTask;
    }

    private Task SubscribeExploreTargetInfo(G_TO_U_EXPLORE_TARGET_INFO body)
    {
        using var packet = PacketMaker.U_TO_C_EXPLORE_TARGET_INFO([body.ExploreTargetInfo]);
        Send(packet);
       
        return Task.CompletedTask;
    }

    private async Task SubscribeEnterInstanceSuccess(G_TO_U_ENTER_INSTANCE_SUCCESS body)
    {
        if (_playerController == null)
        {
            return;
        }
       
        if (body.MapId == MapId.Camp)
        {
            await _playerController.EnterCamp(body.MapSubId);
        }

        var mapInfo = _playerController.CurrentMapInfo;
        if (body.MapId != mapInfo.Item1 || body.MapSubId != mapInfo.Item2)
        {
            return;
        }

        var lastMapInfo = _playerController.LastMapInfo;
        using var packet = PacketMaker.U_TO_C_CHANGE_MAP(lastMapInfo.Item1, mapInfo.Item1, mapInfo.Item2, mapInfo.Item3, mapInfo.Item4);
        Send(packet);
    }
   
    private Task SubscribeCampInfo(G_TO_U_CAMP_INFO body)
    {
        using var packet = PacketMaker.U_TO_C_CAMP_INFO([body.CampInfo]);
        Send(packet);
       
        return Task.CompletedTask;
    }

    private Task SubscribeSocialAction(G_TO_U_SOCIAL_ACTION body)
    {
        using var packet = PacketMaker.U_TO_C_SOCIAL_ACTION(body.PlayerId, body.SocialActionType);
        Send(packet);
       
        return Task.CompletedTask;
    }

    private void BroadcastToMap<T>(GameObjectInfo objectInfo, T payload, Func<GameObjectInfo, int, string> getSubject)
    {
        if (GameMapData.IsCommonMap(objectInfo.MapId))
        {
            var partKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.CurrentCell);
            var targetServerList = MapHelper.GetBoundServerList(objectInfo.MapId, objectInfo.CurrentCell);
            foreach (var server in targetServerList)
            {
                var subject = getSubject(objectInfo, server);
                NatsClient.Publish(subject, MessagePackSerializer.Serialize((partKey, payload)));
            }
            return;
        }

        var instancePartKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);
        var manageServer = MapHelper.GetManageServerId(objectInfo.MapSubId);
        var instanceSubject = getSubject(objectInfo, manageServer);
        NatsClient.Publish(instanceSubject, MessagePackSerializer.Serialize((instancePartKey, payload)));
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

        BroadcastToMap(objectInfo, info, SubjectHelper.GetUpdateInfoSubject);
    }

    public void BroadcastSocialAction(PlayerInfo playerInfo, SocialActionType socialActionType)
    {
        var sendTuple = (playerInfo.PlayerId, socialActionType);
        BroadcastToMap(playerInfo.ObjectInfo, sendTuple, SubjectHelper.GetSocialActionSubject);
    }
    
    public void BroadcastObjectDestroy(GameObjectInfo objectInfo)
    {
        var payload = objectInfo.GetGameObjectKey();
        BroadcastToMap(objectInfo, payload, SubjectHelper.GetDestroyObjectSubject);
    }

    public void Send(IPacket msg)
    {
        if (msg is not Packet packet)
        {
            throw new NotImplementedException();
        }

        _token.Send(packet);
    }

    public void OnRemoved()
    {
        _onLeaveCallback(this);
    }

    private async Task SendToGameServer(Packet msg)
    {
        await CacheHelper.EnqueueAsync(GameServerQueue, msg.ToBytes());
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

            if (_playerController != null)
            {
                Logger.LogInformation("GameUser Removed. PlayerId:{_playerController.PlayerId}", _playerController.PlayerId);
                await _playerController.Dispose();
                using var packet = PacketMaker.U_TO_G_LOGOUT(_playerController.PlayerId);
                await SendToGameServer(packet);
            }
            
            MapObjectController.Dispose();
            await Cts.CancelAsync();
            NatsClient.Close();
            Cts.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Releasing Game User");
        }
        finally
        {
            _token.LockDisconnect.Release();
        }

        return _token;
    }
}