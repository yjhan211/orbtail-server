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
    
    public readonly ChatController ChatController;
    public PlayerController? PlayerController;
    
    public readonly CancellationTokenSource Cts;

    public readonly ICacheHelper CacheHelper;
    public readonly INatsClient NatsClient;
    public readonly IRedLockFactory RedLock;
    public readonly ILogger Logger;
    public MapObjectController MapObjectController;

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
        ChatController = chatController;
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
           { Protocol.C_TO_U_HANDLE_CRAFT, async (bytes) => await HandleMessage<C_TO_U_HANDLE_CRAFT>(bytes, msg => HandlePlayerAction(pc => pc.HandleCraft(msg))) },
           { Protocol.C_TO_U_PUT_MATERIAL, async (bytes) => await HandleMessage<C_TO_U_PUT_MATERIAL>(bytes, msg => HandlePlayerAction(pc => pc.PutMaterial(msg)))},
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
           { Protocol.U_TO_U_DUPLICATE, _ => { ReceiveDuplicate(); return Task.CompletedTask; }},
           { Protocol.G_TO_U_ENVIRONMENT, bytes => PlayerController == null ? Task.CompletedTask : HandleMessage<G_TO_U_ENVIRONMENT>(bytes, PlayerController.SubscribeEnvironment) },
           { Protocol.G_TO_U_TAKE_DAMAGE , bytes => HandleMessage<G_TO_U_TAKE_DAMAGE>(bytes, SubscribeTakeDamage) },
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

            if (PlayerController == null)
            {
                throw new Exception($"[{playerId}] not login");
            }
            
            // if (PlayerController.IsInvalidAction(protocolId))
            // {
            //     throw new Exception($"[{playerId}] in action. protocolId: {protocolId}");
            // }

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
        if (PlayerController == null)
        {
            throw new InvalidOperationException("Player controller is not initialized");
        }
            
        await action(PlayerController);
    }

    private Task HandleHeartBeat(byte[] _)
    {
        using var packet = PacketMaker.U_TO_C_HEART_BEAT(DateTime.UtcNow);
        Send(packet);
        return Task.CompletedTask;
    }
    
    private async Task Login(C_TO_U_LOGIN request)
    {
        if (PlayerController != null)
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
            PlayerController = new PlayerController(this, playerInfo);
            if (playerInfo.IsNew)
            {
                // 기본템 입히기
                var defaultTop = playerInfo.InventoryInfo.ItemDict.First(x => x.Value.ItemId == 104000001);
                var defaultBottom = playerInfo.InventoryInfo.ItemDict.First(x => x.Value.ItemId == 105000001);
                var defaultShoes = playerInfo.InventoryInfo.ItemDict.First(x => x.Value.ItemId == 106000001);
                
                await PlayerController.Wear(new C_TO_U_WEAR_ITEM(){ ItemUid = defaultTop.Value.ItemUid });
                await PlayerController.Wear(new C_TO_U_WEAR_ITEM(){ ItemUid = defaultBottom.Value.ItemUid });
                await PlayerController.Wear(new C_TO_U_WEAR_ITEM(){ ItemUid = defaultShoes.Value.ItemUid });
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
            await PlayerController.SendMail(firstMail);
        }

        var questInfo = await QuestDiary.Load(CacheHelper, tempPlayerId);
        if (questInfo.QuestDict.Count <= 0)
        {
            await PlayerController.StartQuest(100000001);
            // await PlayerController.StartQuest(200000001);
        }

        PlayerController.SendCurrentItems();
        PlayerController.SendCurrentQuests();
        await PlayerController.SendCurrentMails();
        await PlayerController.EnterMap(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.CurrentCell, playerInfo.ObjectInfo.IsFlip, true);
    }

    private async Task<PlayerInfo> Register(long playerId, bool isDummy = false)
    {
        var playerInfo = new PlayerInfo(playerId, isDummy);
        foreach (var (itemId, count) in GameRuleData.DefaultItemList)
        {
            var item = await PlayerInventory.CreateItem(CacheHelper, itemId, count);
            playerInfo.InventoryInfo.ItemDict.Add(item.ItemUid, item);
        }

        if (playerInfo.PlayerId > 1000)
        {
            foreach (var itemInfo in GameItemData.GetAllList())
            {
                var item = await PlayerInventory.CreateItem(CacheHelper, itemInfo.Id, 1);
                playerInfo.InventoryInfo.ItemDict.Add(item.ItemUid, item);
            }
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
        var result = await ChatController.GetChatHistory(chatType);
        foreach (var item in result)
        {
            using var packet = PacketMaker.U_TO_C_CHAT_MSG(item.Item1, item.Item2, item.Item3, item.Item4);
            Send(packet);
        }
    }

    public async Task AppendChat(C_TO_U_CHAT_MSG body)
    {
        if (body.ChatMessage.Length >= Config.MAX_CHAT_LENGTH)
        {
            return;
        }

        if (PlayerController == null)
        {
            return;
        }
        
        if (body.ChatMessage.StartsWith("/ㅎㅎ"))
        {
            var sendTuple = (PlayerController.PlayerId, SocialActionType.LAUGH);
            BroadcastToMap(PlayerController._playerInfo.ObjectInfo, sendTuple, SubjectHelper.GetSocialActionSubject);
            // BroadcastSocialAction();
            // PlayerController.SocialAction(SocialActionType.LAUGH);
            return;
        }
        
        if (body.ChatMessage.StartsWith("/ㄱㄱ"))
        {
            var sendTuple = (PlayerController.PlayerId, SocialActionType.THUMBSUP);
            BroadcastToMap(PlayerController._playerInfo.ObjectInfo, sendTuple, SubjectHelper.GetSocialActionSubject);
            // PlayerController.SocialAction(SocialActionType.LAUGH);
            return;
        }
        
        if (body.ChatMessage.StartsWith("/ㅇㅇ"))
        {
            var sendTuple = (PlayerController.PlayerId, SocialActionType.THUMBSUP);
            BroadcastToMap(PlayerController._playerInfo.ObjectInfo, sendTuple, SubjectHelper.GetSocialActionSubject);
            // PlayerController.SocialAction(SocialActionType.LAUGH);
            return;
        }

        await ChatController.SendChat(PlayerController.PlayerId, PlayerController.PlayerName, body.ChatType, body.ChatMessage, NatsClient);
    }

    private void ReceiveDuplicate()
    {
        using var packet = Packet.Create((int)Protocol.U_TO_U_DUPLICATE);
        Send(packet);
        OnRemoved();
    }

    private Task SubscribeUpdateObject(G_TO_U_UPDATE_OBJECT body)
    {
        if (body.ObjectInfo.ObjectType == ObjectType.PLAYER && PlayerController != null && body.ObjectInfo.ObjectId == PlayerController.PlayerId)
        {
            return Task.CompletedTask;
        }

        MapObjectController.EnqueueUpdateObject(body.ObjectInfo);
        return Task.CompletedTask;
    }

    private Task SubscribeSpawn(G_TO_U_SPAWN body)
    {
        if (PlayerController == null)
        {
            return Task.CompletedTask;
        }

        var objectKeys = body.ObjectKeyList
            .Where(key => key != PlayerController.ObjectKey)
            .ToList();
        var cellsToRemove = body.CellsToRemove.ToList();
    
        if (objectKeys.Count == 0 && cellsToRemove.Count == 0)
        {
            return Task.CompletedTask;
        }
    
        var batchCount = (int)Math.Ceiling((double)Math.Max(objectKeys.Count, cellsToRemove.Count) / Config.BROADCAST_UNIT);
        batchCount = Math.Max(1, batchCount);
    
        for (var i = 0; i < batchCount; i++)
        {
            SendSpawnBatch(objectKeys, cellsToRemove, Config.BROADCAST_UNIT, i, batchCount);
        }
       
        return Task.CompletedTask;
    }

    private void SendSpawnBatch(List<string> objects, List<Cell> cells, int batchSize, int batchIndex, int totalBatches)
    {
        // objects 리스트 처리 - null 체크 및 범위 검증
        var objectBatch = new List<string>();
        var startIndex = batchIndex * batchSize;
        if (startIndex < objects.Count)
        {
            objectBatch = objects
                .Skip(startIndex)
                .Take(Math.Min(batchSize, objects.Count - startIndex))
                .ToList();
        }
    
        // cells 리스트 처리 - null 체크 및 범위 검증
        var cellBatch = new List<Cell>();
        if (startIndex < cells.Count)
        {
            cellBatch = cells
                .Skip(startIndex)
                .Take(Math.Min(batchSize, cells.Count - startIndex))
                .ToList();
        }
    
        var isEnded = batchIndex >= totalBatches - 1;
    
        using var packet = PacketMaker.U_TO_C_SPAWN(objectBatch, isEnded, cellBatch);
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
        if (PlayerController == null)
        {
            return;
        }
       
        if (body.MapId == MapId.Camp)
        {
            await PlayerController.EnterCamp(body.MapSubId);
        }

        var mapInfo = PlayerController.CurrentMapInfo;
        if (body.MapId != mapInfo.Item1 || body.MapSubId != mapInfo.Item2)
        {
            return;
        }

        var lastMapInfo = PlayerController.LastMapInfo;
        using var packet = PacketMaker.U_TO_C_CHANGE_MAP_SUCCESS(lastMapInfo.Item1, mapInfo.Item1, mapInfo.Item2, mapInfo.Item3, mapInfo.Item4);
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
    
    private Task SubscribeTakeDamage(G_TO_U_TAKE_DAMAGE body)
    {
        using var packet = PacketMaker.U_TO_C_TAKE_DAMAGE(body.PlayerId, body.DamageType, body.Damage);;
        Send(packet);
       
        return Task.CompletedTask;
    }
    
    private void BroadcastToMap<T>(GameObjectInfo objectInfo, T payload, Func<GameObjectInfo, int, string> getSubject)
    {
        var serializedPayload = MessagePackSerializer.Serialize(payload);
        if (GameMapData.IsCommonMap(objectInfo.MapId))
        {
            var partKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.TargetCell);
            var targetServerList = MapHelper.GetBoundServerList(objectInfo.MapId, objectInfo.TargetCell);
            foreach (var server in targetServerList)
            {
                var subject = getSubject(objectInfo, server);
                NatsClient.Publish(subject, MessagePackSerializer.Serialize((partKey, objectInfo.ObjectType, serializedPayload)));
            }
            return;
        }

        var instancePartKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);
        var manageServer = MapHelper.GetManageServerId(objectInfo.MapSubId);
        var instanceSubject = getSubject(objectInfo, manageServer);
        NatsClient.Publish(instanceSubject, MessagePackSerializer.Serialize((instancePartKey, objectInfo.ObjectType, serializedPayload)));
    }
    
    public virtual void BroadcastUpdateInfo<T>(T info) where T : IMessagePackObject?
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

    public virtual void BroadcastTakeDamage(PlayerInfo playerInfo, DamageType damageType, int damage)
    {
        var sendTuple = (playerInfo.PlayerId, (damageType, damage));
        BroadcastToMap(playerInfo.ObjectInfo, sendTuple, SubjectHelper.GetTakeDamageSubject);
    }

    public virtual void BroadcastSocialAction(PlayerInfo playerInfo, SocialActionType socialActionType)
    {
        var sendTuple = (playerInfo.PlayerId, socialActionType);
        BroadcastToMap(playerInfo.ObjectInfo, sendTuple, SubjectHelper.GetSocialActionSubject);
    }
    
    public virtual void BroadcastObjectDestroy(GameObjectInfo objectInfo)
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

            if (PlayerController != null)
            {
                Logger.LogInformation("GameUser Removed. PlayerId:{_playerController.PlayerId}", PlayerController.PlayerId);
                await PlayerController.Dispose();
                using var packet = PacketMaker.U_TO_G_LOGOUT(PlayerController.PlayerId);
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