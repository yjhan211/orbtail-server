using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.interfaces;
using network.packets;
using network.routing;
using user_server.services;

namespace user_server.network;

public sealed class GameSession : SessionBase
{
    private readonly IMatchingManager _matchingManager;
    private readonly Action<long, GameSession> _onSessionRegistered;
    private readonly IPlayerService _playerService;
    private readonly IProtocolRouter _subscribeRouter;

    public GameSession(
        UserToken token,
        ILogger logger,
        ICacheHelper cacheHelper,
        IRedLockFactory redLock,
        IPlayerService playerService,
        IMatchingManager matchingManager,
        Action<long, GameSession> onSessionRegistered)
        : base(token, logger, cacheHelper, redLock)
    {
        _playerService = playerService;
        _matchingManager = matchingManager;
        _onSessionRegistered = onSessionRegistered;

        _subscribeRouter = new ProtocolRouter();
        // ReSharper disable once VirtualMemberCallInConstructor
        InitializeProtocolHandlers();
    }

    public new long? PlayerId { get; private set; }
    public PlayerInfo? PlayerInfo { get; private set; }

    protected override void InitializeProtocolHandlers()
    {
        // 클라이언트 프로토콜
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_HEART_BEAT, HandleHeartBeat);
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_LOGIN,
            async bytes => await HandleMessage<C_TO_U_LOGIN>(bytes, Login));
        // C_TO_U_PLAYER_INFO는 세션 기반 게임에서는 GameServer에서 처리 (G_TO_C_PLAYER_INFO)
        // ProtocolRouter.RegisterHandler(Protocol.C_TO_U_PLAYER_INFO, async (bytes) => await HandleMessage<C_TO_U_PLAYER_INFO>(bytes, GetPlayerInfo));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_WEAR_ITEM,
            async bytes => await HandleMessage<C_TO_U_WEAR_ITEM>(bytes, WearItem));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_USE_ITEM,
            async bytes => await HandleMessage<C_TO_U_USE_ITEM>(bytes, UseItem));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_CHAT_MSG,
            async bytes => await HandleMessage<C_TO_U_CHAT_MSG>(bytes, AppendChat));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_CHAT_LOG, async _ => await SendChatHistory(ChatType.ALL));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_SET_NAME,
            async bytes => await HandleMessage<C_TO_U_SET_NAME>(bytes, SetName));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_QUEST_INCREASE,
            async bytes => await HandleMessage<C_TO_U_QUEST_INCREASE>(bytes, IncreaseQuestCount));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_QUEST_SUCCESS,
            async bytes => await HandleMessage<C_TO_U_QUEST_SUCCESS>(bytes, CompleteQuest));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_MAIL_LIST, async _ => await SendMailList());
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_MAIL_RECEIVE,
            async bytes => await HandleMessage<C_TO_U_MAIL_RECEIVE>(bytes, ReceiveMail));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_MATCHING,
            async bytes => await HandleMessage<C_TO_U_MATCHING>(bytes, HandleMatching));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_MATCHING_CANCEL, async _ => await HandleMatchingCancel());

        // 구독 프로토콜
        _subscribeRouter.RegisterHandler(Protocol.U_TO_C_CHAT_MSG,
            bytes => HandleMessage<U_TO_C_CHAT_MSG>(bytes, SubscribeChatMsg));
        _subscribeRouter.RegisterHandler(Protocol.U_TO_U_DUPLICATE, _ =>
        {
            ReceiveDuplicate();
            return Task.CompletedTask;
        });
    }

    protected override bool ShouldSkipLogging(Protocol protocolId)
    {
        return protocolId == Protocol.C_TO_U_HEART_BEAT;
    }

    private Task HandleHeartBeat(byte[] _)
    {
        using var packet = PacketMaker.U_TO_C_HEART_BEAT(DateTime.UtcNow);
        Send(packet);
        return Task.CompletedTask;
    }

    private async Task Login(C_TO_U_LOGIN msg)
    {
        try
        {
            Logger.LogInformation("Login request received: AccountToken={AccountToken}", msg.AccountToken);

            // AccountToken이 없거나 파싱 실패시 Redis INCR로 새 PlayerId 생성
            if (string.IsNullOrEmpty(msg.AccountToken) || !long.TryParse(msg.AccountToken, out long playerId))
            {
                // Redis INCR을 사용해 1부터 순차 증가하는 PlayerId 생성
                playerId = await CacheHelper.StringIncrementAsync("player_id_counter");
                Logger.LogInformation("Generated new PlayerId from Redis: {PlayerId}", playerId);
            }

            PlayerId = playerId;
            Logger.LogInformation("PlayerId set to {PlayerId}", PlayerId);

            await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
            Logger.LogInformation("Player lock acquired for PlayerId={PlayerId}", PlayerId);

            PlayerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

            if (PlayerInfo == null)
            {
                // 신규 플레이어 생성
                Logger.LogInformation("Creating new player: PlayerId={PlayerId}", PlayerId);
                PlayerInfo = new PlayerInfo(PlayerId.Value, false);
                await PlayerInfo.Save(CacheHelper);
                Logger.LogInformation("New player created and saved: PlayerId={PlayerId}", PlayerId);
            }
            else
            {
                Logger.LogInformation("Existing player loaded: PlayerId={PlayerId}, Name={Name}", PlayerId,
                    PlayerInfo.Name);
            }

            // 신규 플레이어 초기 아이템 지급
            if (PlayerInfo.IsNew) await SetupNewPlayer(PlayerInfo);

            // 세션 등록
            _onSessionRegistered(PlayerId.Value, this);
            Logger.LogInformation("Session registered for PlayerId={PlayerId}", PlayerId);

            // TODO: SubjectHelper에 GetDuplicateLoginSubject, GetPlayerSubject 추가 필요
            // 중복 로그인 체크 (다른 세션에 중복 알림 전송)
            // var duplicateSubject = SubjectHelper.GetDuplicateLoginSubject(PlayerId.Value);
            // _natsClient.Publish(duplicateSubject, Array.Empty<byte>());

            // NATS 구독 시작
            // var playerSubject = SubjectHelper.GetPlayerSubject(PlayerId.Value);
            // _natsClient.Subscribe(playerSubject, (subject, body) => _ = OnMessageFromNatsWrapper(body));

            // 로그인 응답 전송
            Logger.LogInformation("Creating login packet for PlayerId={PlayerId}", PlayerId);
            using var loginPacket = PacketMaker.U_TO_C_LOGIN(PlayerInfo);
            Logger.LogInformation("Sending U_TO_C_LOGIN packet for PlayerId={PlayerId}, Packet size={Size}", PlayerId,
                loginPacket.ToBytes().Length);
            Send(loginPacket);

            // 인벤토리 아이템 리스트 전송 (청크 단위로 분할)
            if (PlayerInfo.InventoryInfo.ItemDict.Count > 0)
            {
                const int chunkSize = 20; // 한 번에 20개씩 전송
                var itemList = PlayerInfo.InventoryInfo.ItemDict.ToList();
                int totalChunks = (itemList.Count + chunkSize - 1) / chunkSize;

                for (int i = 0; i < totalChunks; i++)
                {
                    var chunk = itemList.Skip(i * chunkSize).Take(chunkSize).ToDictionary(x => x.Key, x => x.Value);
                    bool isEnd = i == totalChunks - 1;

                    using var itemListPacket = PacketMaker.U_TO_C_INVENTORY_ITEM_LIST(chunk, isEnd);
                    Send(itemListPacket);
                }

                Logger.LogInformation(
                    "Sent inventory item list in {ChunkCount} packets: PlayerId={PlayerId}, ItemCount={Count}",
                    totalChunks, PlayerId, PlayerInfo.InventoryInfo.ItemDict.Count);
            }

            Logger.LogInformation("Player {PlayerId} logged in successfully", PlayerId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Login failed for player {AccountToken}", msg.AccountToken);
            SendErrorResponse(ErrorCode.SERVER_INTERNAL_ERROR, "로그인 처리 중 오류가 발생했습니다");
            Disconnect();
        }
    }

    private async Task SetupNewPlayer(PlayerInfo playerInfo)
    {
        try
        {
            Logger.LogInformation("Setting up new player: PlayerId={PlayerId}", playerInfo.PlayerId);

            // 아이템 UID 카운터 가져오기
            long itemUidCounter = await CacheHelper.StringIncrementAsync("item_uid_counter");

            // 기본 아이템 3종 먼저 추가 (Top, Bottom, Shoes)
            var defaultTop = new ItemInfo(itemUidCounter++, 104000001, 1);
            var defaultBottom = new ItemInfo(itemUidCounter++, 105000001, 1);
            var defaultShoes = new ItemInfo(itemUidCounter++, 106000001, 1);

            playerInfo.InventoryInfo.ItemDict.Add(defaultTop.ItemUid, defaultTop);
            playerInfo.InventoryInfo.ItemDict.Add(defaultBottom.ItemUid, defaultBottom);
            playerInfo.InventoryInfo.ItemDict.Add(defaultShoes.ItemUid, defaultShoes);

            Logger.LogInformation("Added default items: Top={TopUid}, Bottom={BottomUid}, Shoes={ShoesUid}",
                defaultTop.ItemUid, defaultBottom.ItemUid, defaultShoes.ItemUid);

            // 기본 아이템 착용 (직접 처리)
            defaultTop.IsWear = true;
            defaultBottom.IsWear = true;
            defaultShoes.IsWear = true;

            playerInfo.WearItemIdList.Add(defaultTop.ItemId);
            playerInfo.WearItemIdList.Add(defaultBottom.ItemId);
            playerInfo.WearItemIdList.Add(defaultShoes.ItemId);

            Logger.LogInformation("Default items equipped for PlayerId={PlayerId}, WearItemCount={Count}",
                playerInfo.PlayerId, playerInfo.WearItemIdList.Count);

            // 테스트용 코스튬 아이템 전부 지급
            var allItems = GameItemData.GetAllList();

            foreach (var itemInfoData in allItems.Where(x => x.IsEquipment))
            {
                var equipType = GameItemData.GetEquipType(itemInfoData.Id);
                switch (equipType)
                {
                    case EquipType.HEAD:
                    case EquipType.FACE:
                    case EquipType.HAT:
                    case EquipType.TOP:
                    case EquipType.BOTTOM:
                    case EquipType.SHOES:
                        // 기본 아이템 3종은 이미 추가했으므로 스킵
                        if (itemInfoData.Id == 104000001 || itemInfoData.Id == 105000001 ||
                            itemInfoData.Id == 106000001) continue;

                        var item = new ItemInfo(itemUidCounter++, itemInfoData.Id, 1);
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

            // 저장
            playerInfo.IsNew = false;
            await playerInfo.Save(CacheHelper);

            Logger.LogInformation("New player setup complete: PlayerId={PlayerId}, Total items={Count}",
                playerInfo.PlayerId, playerInfo.InventoryInfo.ItemDict.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to setup new player: PlayerId={PlayerId}", playerInfo.PlayerId);
        }
    }

    // ========== PlayerService 위임 ==========

    private async Task WearItem(C_TO_U_WEAR_ITEM msg)
    {
        if (PlayerId == null) return;

        var (errorCode, playerInfo) = await _playerService.WearItem(PlayerId.Value, msg);

        if (errorCode == ErrorCode.SUCCESS && playerInfo != null)
        {
            using var packet = PacketMaker.U_TO_C_WEAR_ITEM(playerInfo);
            Send(packet);
        }
        else
        {
            SendErrorResponse(errorCode, "아이템 착용 실패");
        }
    }

    private async Task UseItem(C_TO_U_USE_ITEM msg)
    {
        if (PlayerId == null) return;

        var (errorCode, playerInfo) = await _playerService.UseItem(PlayerId.Value, msg);

        if (errorCode == ErrorCode.SUCCESS && playerInfo != null)
        {
            using var packet = PacketMaker.U_TO_C_USE_ITEM(playerInfo);
            Send(packet);
        }
        else
        {
            SendErrorResponse(errorCode, "아이템 사용 실패");
        }
    }

    private async Task SetName(C_TO_U_SET_NAME msg)
    {
        if (PlayerId == null) return;

        var (errorCode, playerInfo) = await _playerService.SetName(PlayerId.Value, msg);

        if (playerInfo != null)
        {
            using var packet = PacketMaker.U_TO_C_SET_NAME(errorCode, playerInfo);
            Send(packet);
        }
        else
        {
            SendErrorResponse(errorCode, "이름 변경 실패");
        }
    }

    private async Task IncreaseQuestCount(C_TO_U_QUEST_INCREASE msg)
    {
        if (PlayerId == null) return;

        _ = await _playerService.IncreaseQuestCount(PlayerId.Value, msg);
        // TODO: Send quest update packet
    }

    private async Task CompleteQuest(C_TO_U_QUEST_SUCCESS msg)
    {
        if (PlayerId == null) return;

        var (errorCode, playerInfo) = await _playerService.CompleteQuest(PlayerId.Value, msg);

        if (playerInfo != null)
        {
            using var packet = PacketMaker.U_TO_C_QUEST_SUCCESS(msg.QuestId, errorCode);
            Send(packet);
        }
        else
        {
            SendErrorResponse(errorCode, "퀘스트 완료 실패");
        }
    }

    private async Task SendMailList()
    {
        if (PlayerId == null) return;

        var (errorCode, mailDict) = await _playerService.GetMailList(PlayerId.Value);

        if (errorCode == ErrorCode.SUCCESS && mailDict != null)
        {
            using var packet = PacketMaker.U_TO_C_MAIL_LIST(mailDict, true);
            Send(packet);
        }
        else
        {
            SendErrorResponse(errorCode, "메일 목록 조회 실패");
        }
    }

    private async Task ReceiveMail(C_TO_U_MAIL_RECEIVE msg)
    {
        if (PlayerId == null) return;

        var (errorCode, playerInfo) = await _playerService.ReceiveMail(PlayerId.Value, msg);

        if (playerInfo != null)
        {
            using var packet = PacketMaker.U_TO_C_MAIL_RECEIVE(msg.MailUid, errorCode);
            Send(packet);
        }
        else
        {
            SendErrorResponse(errorCode, "메일 수신 실패");
        }
    }

    // ========== 채팅 ==========

    private async Task AppendChat(C_TO_U_CHAT_MSG msg)
    {
        if (PlayerId == null || PlayerInfo == null) return;

        Logger.LogInformation($"Chat from {PlayerId}: {msg.ChatMessage}");

        // TODO: SubjectHelper에 GetChatSubject 추가 필요, GameObjectInfo에서 이름 필드 확인 필요
        // var chatSubject = SubjectHelper.GetChatSubject(ChatType.ALL);
        // var chatPacket = PacketMaker.U_TO_C_CHAT_MSG(ChatType.ALL, PlayerId.Value, PlayerInfo.ObjectInfo.Name, msg.ChatMessage);
        // _natsClient.Publish(chatSubject, MessagePackSerializer.Serialize((Protocol.U_TO_C_CHAT_MSG, chatPacket.ToBytes())));
        await Task.CompletedTask;
    }

    private Task SubscribeChatMsg(U_TO_C_CHAT_MSG msg)
    {
        using var packet = PacketMaker.U_TO_C_CHAT_MSG(msg.ChatType, msg.PlayerId, msg.Name, msg.ChatMessage);
        Send(packet);
        return Task.CompletedTask;
    }

    private async Task SendChatHistory(ChatType _)
    {
        // TODO: Implement chat history
        await Task.CompletedTask;
    }

    // ========== 매칭 ==========

    private async Task HandleMatching(C_TO_U_MATCHING msg)
    {
        if (PlayerId == null) return;

        var errorCode = await _matchingManager.AddToQueue(PlayerId.Value, this);

        using var packet = PacketMaker.U_TO_C_MATCHING(errorCode);
        Send(packet);
    }

    private async Task HandleMatchingCancel()
    {
        if (PlayerId == null) return;

        var errorCode = await _matchingManager.CancelMatching(PlayerId.Value);

        using var packet = PacketMaker.U_TO_C_MATCHING_CANCEL(errorCode);
        Send(packet);
    }

    // ========== 기타 ==========

    private void ReceiveDuplicate()
    {
        Logger.LogWarning($"Duplicate login detected for player {PlayerId}");
        Disconnect();
    }

    public override void Send(IPacket packet)
    {
        try
        {
            if (packet is Packet p)
            {
                Token.Send(p);
                if (p.ProtocolId != (int)Protocol.U_TO_C_HEART_BEAT)
                    Logger.LogInformation("Packet sent: Protocol={Protocol}, PlayerId={PlayerId}",
                        (Protocol)p.ProtocolId, PlayerId);
            }
            else
            {
                Logger.LogWarning("Invalid packet type: {Type}, PlayerId={PlayerId}", packet.GetType().Name, PlayerId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send packet: PlayerId={PlayerId}", PlayerId);
        }
    }

    public string GetChannelName()
    {
        // TODO: SubjectHelper에 GetPlayerSubject 추가 필요
        return $"player.{PlayerId ?? 0}";
    }

    private void Disconnect()
    {
        Token.Disconnect();
    }

    protected override void SendErrorResponse(ErrorCode errorCode, string message)
    {
        try
        {
            using var packet = PacketMaker.U_TO_C_ERROR(errorCode, message);
            Send(packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "에러 응답 전송 실패: PlayerId={PlayerId}", PlayerId);
        }
    }

    public override void OnRemoved()
    {
        Logger.LogInformation("Session removed: PlayerId={PlayerId}", PlayerId);

        // 매칭 큐에서 제거
        if (PlayerId.HasValue)
        {
            _ = _matchingManager.CancelMatching(PlayerId.Value);
        }
    }

    public override void OnDisconnect()
    {
        Logger.LogInformation("Session disconnected: PlayerId={PlayerId}", PlayerId);

        // 매칭 큐에서 제거
        if (PlayerId.HasValue)
        {
            _ = _matchingManager.CancelMatching(PlayerId.Value);
        }
    }
}
