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
using user_server.application.services;
using user_server.domain.player;
using user_server.infrastructure.protocols;

namespace user_server.infrastructure.network;

public class GameSession : IPeer
{
    private const string GlobalSubscribeChannel = "all";
    private const string TempPlayerIdKey = "temp_player_id";

    private readonly UserToken _token;
    private readonly SemaphoreSlim _userLock;
    private readonly Action<GameSession> _onLeaveCallback;
    private readonly IProtocolRouter _protocolRouter;
    private readonly IProtocolRouter _subscribeRouter;

    public readonly ChatController ChatController;
    public Player? Player;

    public readonly CancellationTokenSource Cts;

    public readonly ICacheHelper CacheHelper;
    public readonly INatsClient NatsClient;
    public readonly IRedLockFactory RedLock;
    public readonly ILogger Logger;
    public MapObjectController MapObjectController;
    private readonly MatchingManager? _matchingManager;
    private readonly IServerConfig _serverConfig;


    public GameSession(UserToken token, IRedLockFactory redLock, INatsClient natsClient, ILogger logger, ICacheHelper cacheHelper, Action<GameSession> onLeaveCallback, ChatController chatController, MatchingManager? matchingManager, IServerConfig serverConfig)
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
        _matchingManager = matchingManager;
        _serverConfig = serverConfig;

        _protocolRouter = new ProtocolRouter(logger);
        _subscribeRouter = new ProtocolRouter(logger);

        InitializeProtocolHandlers();
        InitializeSubscribeHandlers();

        Logger.LogInformation("Create GameSession Success!");
    }

    private void InitializeProtocolHandlers()
    {
        // Client Protocol Handlers
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_HEART_BEAT, HandleHeartBeat);
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_LOGIN, async (bytes) => await HandleMessage<C_TO_U_LOGIN>(bytes, Login));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_CHANGE_MAP_SUCCESS, async (_) => await HandlePlayerAction(pc => pc.Spawn()));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_CHAT_LOG, async (_) => await SendChatHistory(ChatType.ALL));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_MOVE, async (bytes) => await HandleMessage<C_TO_U_MOVE>(bytes, msg => HandlePlayerAction(pc => pc.RequestMove(msg))));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_PLAYER_INFO, async (bytes) => await HandleMessage<C_TO_U_PLAYER_INFO>(bytes, GetPlayerInfo));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_EXPLORE_TARGET_INFO, async (bytes) => await HandleMessage<C_TO_U_EXPLORE_TARGET_INFO>(bytes, GetExploreTargetInfo));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_WEAR_ITEM, async (bytes) => await HandleMessage<C_TO_U_WEAR_ITEM>(bytes, msg => HandlePlayerAction(pc => pc.WearItem(msg))));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_USE_ITEM, async (bytes) => await HandleMessage<C_TO_U_USE_ITEM>(bytes, msg => HandlePlayerAction(pc => pc.UseItem(msg))));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_CHANGE_MAP, async (bytes) => await HandleMessage<C_TO_U_CHANGE_MAP>(bytes, msg => HandlePlayerAction(pc => pc.ChangeMap(msg))));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_EXPLORE, async (bytes) => await HandleMessage<C_TO_U_EXPLORE>(bytes, msg => HandlePlayerAction(pc => pc.Explore(msg))));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_CHAT_MSG, async (bytes) => await HandleMessage<C_TO_U_CHAT_MSG>(bytes, AppendChat));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_SET_NAME, async (bytes) => await HandleMessage<C_TO_U_SET_NAME>(bytes, msg => HandlePlayerAction(pc => pc.SetName(msg))));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_SOCIAL_ACTION, async (bytes) => await HandleMessage<C_TO_U_SOCIAL_ACTION>(bytes, msg => HandlePlayerAction(pc => pc.PerformSocialAction(msg))));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_QUEST_INCREASE, async (bytes) => await HandleMessage<C_TO_U_QUEST_INCREASE>(bytes, msg => HandlePlayerAction(pc => pc.IncreaseQuestCount(msg))));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_QUEST_SUCCESS, async (bytes) => await HandleMessage<C_TO_U_QUEST_SUCCESS>(bytes, msg => HandlePlayerAction(pc => pc.CompleteQuest(msg))));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_MAIL_LIST, async (_) => await HandlePlayerAction(pc => pc.SendCurrentMails()));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_MAIL_RECEIVE, async (bytes) => await HandleMessage<C_TO_U_MAIL_RECEIVE>(bytes, msg => HandlePlayerAction(pc => pc.ReceiveMail(msg))));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_OBJECT_INFO, async (bytes) => await HandleMessage<C_TO_U_OBJECT_INFO>(bytes, MapObjectController.GetObjectInfo));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_MATCHING, async (bytes) => await HandleMessage<C_TO_U_MATCHING>(bytes, HandleMatching));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_MATCHING_CANCEL, async (_) => await HandleMatchingCancel());
    }

    private void InitializeSubscribeHandlers()
    {
        // Subscribe Protocol Handlers (NATS)
        _subscribeRouter.RegisterHandler(Protocol.G_TO_U_UPDATE_OBJECT, bytes => HandleMessage<G_TO_U_UPDATE_OBJECT>(bytes, SubscribeUpdateObject));
        _subscribeRouter.RegisterHandler(Protocol.G_TO_U_SPAWN, bytes => HandleMessage<G_TO_U_SPAWN>(bytes, SubscribeSpawn));
        _subscribeRouter.RegisterHandler(Protocol.G_TO_U_DESTROY, bytes => HandleMessage<G_TO_U_DESTROY>(bytes, SubscribeDestroy));
        _subscribeRouter.RegisterHandler(Protocol.U_TO_C_CHAT_MSG, bytes => HandleMessage<U_TO_C_CHAT_MSG>(bytes, SubscribeChatMsg));
        _subscribeRouter.RegisterHandler(Protocol.G_TO_U_PLAYER_INFO, bytes => HandleMessage<G_TO_U_PLAYER_INFO>(bytes, SubscribePlayerInfo));
        _subscribeRouter.RegisterHandler(Protocol.G_TO_U_EXPLORE_TARGET_INFO, bytes => HandleMessage<G_TO_U_EXPLORE_TARGET_INFO>(bytes, SubscribeExploreTargetInfo));
        _subscribeRouter.RegisterHandler(Protocol.G_TO_U_ENTER_INSTANCE_SUCCESS, bytes => HandleMessage<G_TO_U_ENTER_INSTANCE_SUCCESS>(bytes, SubscribeEnterInstanceSuccess));
        _subscribeRouter.RegisterHandler(Protocol.G_TO_U_SOCIAL_ACTION, bytes => HandleMessage<G_TO_U_SOCIAL_ACTION>(bytes, SubscribeSocialAction));
        _subscribeRouter.RegisterHandler(Protocol.U_TO_U_DUPLICATE, _ => { ReceiveDuplicate(); return Task.CompletedTask; });
        _subscribeRouter.RegisterHandler(Protocol.G_TO_U_ENVIRONMENT, bytes => Player == null ? Task.CompletedTask : HandleMessage<G_TO_U_ENVIRONMENT>(bytes, Player.SubscribeEnvironment));
        _subscribeRouter.RegisterHandler(Protocol.G_TO_U_TAKE_DAMAGE, bytes => HandleMessage<G_TO_U_TAKE_DAMAGE>(bytes, SubscribeTakeDamage));
        _subscribeRouter.RegisterHandler(Protocol.U_TO_C_MATCHING_SUCCESS, bytes => HandleMessage<U_TO_C_MATCHING_SUCCESS>(bytes, SubscribeMatchingSuccess));
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

            if (protocolId != Protocol.C_TO_U_HEART_BEAT)
            {
                Logger.LogInformation("[{PlayerId}] {ProtocolId}", playerId, protocolId);
            }

            // Non-auth protocols can be processed without authentication
            if (_protocolRouter.IsNonAuthProtocol(protocolId))
            {
                await _protocolRouter.RouteAsync(protocolId, body);
                return;
            }

            // Check authentication for other protocols
            if (Player == null)
            {
                throw new Exception($"[{playerId}] not login");
            }

            // if (Player.IsInvalidAction(protocolId))
            // {
            //     throw new Exception($"[{playerId}] in action. protocolId: {protocolId}");
            // }

            await _protocolRouter.RouteAsync(protocolId, body);
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

            await _subscribeRouter.RouteAsync(protocolId, body);
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

    private async Task HandlePlayerAction(Func<Player, Task> action)
    {
        if (Player == null)
        {
            throw new InvalidOperationException("Player controller is not initialized");
        }
            
        await action(Player);
    }

    private Task HandleHeartBeat(byte[] _)
    {
        // using var packet = PacketMaker.U_TO_C_HEART_BEAT(DateTime.UtcNow);
        // Send(packet);
        return Task.CompletedTask;
    }
    
    private async Task Login(C_TO_U_LOGIN request)
    {
        if (Player != null)
        {
            throw new Exception($"Already Initialized. {request.AccountToken}");
        }

        var isDummy = false;
        if (!long.TryParse(request.AccountToken, out var tempPlayerId))
        {
            tempPlayerId = await CacheHelper.StringIncrementAsync(TempPlayerIdKey);
            // isDummy = true;
        }

        PlayerInfo playerInfo;
        await using (await PlayerInfo.Lock(RedLock, tempPlayerId))
        {
            playerInfo = await PlayerInfo.Load(CacheHelper, tempPlayerId) ?? await Register(tempPlayerId, isDummy);
            Player = new Player(this, playerInfo);
            if (playerInfo.IsNew)
            {
                // 기본템 입히기
                var defaultTop = playerInfo.InventoryInfo.ItemDict.First(x => x.Value.ItemId == 104000001);
                var defaultBottom = playerInfo.InventoryInfo.ItemDict.First(x => x.Value.ItemId == 105000001);
                var defaultShoes = playerInfo.InventoryInfo.ItemDict.First(x => x.Value.ItemId == 106000001);

                var defaultItemUids = new List<long>
                {
                    defaultTop.Value.ItemUid,
                    defaultBottom.Value.ItemUid,
                    defaultShoes.Value.ItemUid
                };

                await Player.WearItem(new C_TO_U_WEAR_ITEM(){ ItemUidList = defaultItemUids });
                
                // 테스트로 코스튬 아이템 전부 지급
                var allItems = GameItemData.GetAllList();
                foreach (var itemInfo in allItems.Where(itemInfo => itemInfo.IsEquipment))
                {
                    var equipType = GameItemData.GetEquipType(itemInfo.Id);
                    switch (equipType)
                    {
                        case EquipType.HEAD:
                        case EquipType.FACE: 
                        case EquipType.HAT:
                        case EquipType.TOP:
                        case EquipType.BOTTOM:
                        case EquipType.SHOES:
                            var item = await PlayerInventory.CreateItem(CacheHelper, itemInfo.Id, 1);
                            playerInfo.InventoryInfo.ItemDict.Add(item.ItemUid, item);
                            break;

                        case EquipType.NONE:
                        case EquipType.TOOL:
                        case EquipType.PILLOW:
                        case EquipType.BEDDING:
                        default:
                            break;
                    }
                }
            }
            
            using var duplicatePacket = Packet.Create((int)Protocol.U_TO_U_DUPLICATE);
            NatsClient.Publish(playerInfo.ObjectInfo.GetGameObjectKey(), duplicatePacket.ToBytes());
            
            await playerInfo.Save(CacheHelper);
            await playerInfo.ObjectInfo.Save(CacheHelper);
        }
        
        SubscribeHandler(playerInfo.ObjectInfo.GetGameObjectKey(), OnMessageFromSubscribe);
        SubscribeHandler(GlobalSubscribeChannel, OnMessageFromSubscribe);
        
        using var loginPacket = PacketMaker.U_TO_C_LOGIN(playerInfo);
        Send(loginPacket);

        // var mailInfo = await MailBox.Load(CacheHelper, tempPlayerId);
        // if (mailInfo.MailDict.Count <= 0)
        // {
        //     var firstMail = await PlayerMailBox.CreateMail(CacheHelper, 1);
        //     await Player.SendMail(firstMail);
        // }

        // var questInfo = await QuestDiary.Load(CacheHelper, tempPlayerId);
        // if (questInfo.QuestDict.Count <= 0)
        // {
        //     await Player.StartQuest(100000001);
        //     // await Player.StartQuest(200000001);
        // }

        Player.SendCurrentItems();
        // Player.SendCurrentQuests();
        // await Player.SendCurrentMails();
        await Player.EnterMap(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.CurrentCell, playerInfo.ObjectInfo.IsFlip, true);
    }

    private async Task<PlayerInfo> Register(long playerId, bool isDummy = false)
    {
        var playerInfo = new PlayerInfo(playerId, isDummy);
        foreach (var (itemId, count) in GameRuleData.DefaultItemList)
        {
            var item = await PlayerInventory.CreateItem(CacheHelper, itemId, count);
            playerInfo.InventoryInfo.ItemDict.Add(item.ItemUid, item);
        }

        if (playerInfo.PlayerId > PlayerConstants.DUMMY_PLAYER_ID_THRESHOLD)
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

        if (Player == null)
        {
            return;
        }
        
        if (body.ChatMessage.StartsWith("/ㅎㅎ"))
        {
            var sendTuple = (Player.PlayerId, SocialActionType.LAUGH);
            BroadcastToMap(Player.PlayerInfo.ObjectInfo, sendTuple, SubjectHelper.GetSocialActionSubject);
            return;
        }
        
        if (body.ChatMessage.StartsWith("/ㄱㄱ"))
        {
            var sendTuple = (Player.PlayerId, SocialActionType.THUMBSUP);
            BroadcastToMap(Player.PlayerInfo.ObjectInfo, sendTuple, SubjectHelper.GetSocialActionSubject);
            return;
        }
        
        if (body.ChatMessage.StartsWith("/ㅇㅇ"))
        {
            Player.PlayerInfo.State = PlayerState.SITGROUND;
            await Player.PlayerInfo.Save(CacheHelper);
            BroadcastUpdateInfo(Player.PlayerInfo);
            return;
        }

        await ChatController.SendChat(Player.PlayerId, Player.PlayerName, body.ChatType, body.ChatMessage, NatsClient);
    }

    private async Task HandleMatching(C_TO_U_MATCHING body)
    {
        if (_matchingManager == null)
        {
            Logger.LogError("MatchingManager가 초기화되지 않았습니다");
            using var errorPacket = PacketMaker.U_TO_C_MATCHING(ErrorCode.FATAL);
            Send(errorPacket);
            return;
        }

        if (Player == null)
        {
            Logger.LogError("PlayerController가 초기화되지 않았습니다");
            using var errorPacket = PacketMaker.U_TO_C_MATCHING(ErrorCode.FATAL);
            Send(errorPacket);
            return;
        }

        var result = await _matchingManager.AddToQueue(Player.PlayerId, this);
        using var packet = PacketMaker.U_TO_C_MATCHING(result);
        Send(packet);
    }

    private async Task HandleMatchingCancel()
    {
        if (_matchingManager == null)
        {
            Logger.LogError("MatchingManager가 초기화되지 않았습니다");
            using var errorPacket = PacketMaker.U_TO_C_MATCHING_CANCEL(ErrorCode.FATAL);
            Send(errorPacket);
            return;
        }

        if (Player == null)
        {
            Logger.LogError("PlayerController가 초기화되지 않았습니다");
            using var errorPacket = PacketMaker.U_TO_C_MATCHING_CANCEL(ErrorCode.FATAL);
            Send(errorPacket);
            return;
        }

        var result = await _matchingManager.CancelMatching(Player.PlayerId);
        using var packet = PacketMaker.U_TO_C_MATCHING_CANCEL(result);
        Send(packet);
    }

    private void ReceiveDuplicate()
    {
        using var packet = Packet.Create((int)Protocol.U_TO_U_DUPLICATE);
        Send(packet);
        OnRemoved();
    }

    private Task SubscribeUpdateObject(G_TO_U_UPDATE_OBJECT body)
    {
        if (body.ObjectInfo.ObjectType == ObjectType.PLAYER && Player != null && body.ObjectInfo.ObjectId == Player.PlayerId)
        {
            return Task.CompletedTask;
        }

        MapObjectController.EnqueueUpdateObject(body.ObjectInfo);
        return Task.CompletedTask;
    }

    private Task SubscribeSpawn(G_TO_U_SPAWN body)
    {
        if (Player == null)
        {
            return Task.CompletedTask;
        }

        var objectKeys = body.ObjectKeyList
            .Where(key => key != Player.ObjectKey)
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
        if (Player == null)
        {
            return;
        }
       
        if (body.MapId == MapId.Camp)
        {
            await Player.EnterCamp(body.MapSubId);
        }

        var mapInfo = Player.CurrentMapInfo;
        if (body.MapId != mapInfo.Item1 || body.MapSubId != mapInfo.Item2)
        {
            return;
        }

        var lastMapInfo = Player.LastMapInfo;
        using var packet = PacketMaker.U_TO_C_CHANGE_MAP_SUCCESS(lastMapInfo.Item1, mapInfo.Item1, mapInfo.Item2, mapInfo.Item3, mapInfo.Item4);
        Send(packet);
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

    private Task SubscribeMatchingSuccess(U_TO_C_MATCHING_SUCCESS body)
    {
        using var packet = PacketMaker.U_TO_C_MATCHING_SUCCESS(body.MatchingId, body.MapId, body.MapSubId, body.SpawnPosition);
        Send(packet);

        return Task.CompletedTask;
    }

    private void BroadcastToMap<T>(GameObjectInfo objectInfo, T payload, Func<GameObjectInfo, int, string> getSubject)
    {
        var serializedPayload = MessagePackSerializer.Serialize(payload);
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

    public string GetChannelName()
    {
        if (Player == null)
        {
            throw new InvalidOperationException("PlayerController is not initialized");
        }

        return Player.ObjectKey;
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

    private void SendToGameServer(Packet msg)
    {
        // Redis Queue 대신 NATS를 사용하여 GameServer에 메시지 전송
        var subject = SubjectHelper.GetLogoutSubject(_serverConfig.ServerId);
        NatsClient.Publish(subject, msg.ToBytes());
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

            if (Player != null)
            {
                Logger.LogInformation("GameUser Removed. PlayerId:{_playerController.PlayerId}", Player.PlayerId);
                await Player.Dispose();
                using var packet = PacketMaker.U_TO_G_LOGOUT(Player.PlayerId);
                SendToGameServer(packet);
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
