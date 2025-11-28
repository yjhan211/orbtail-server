using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.core;
using network.helpers;
using network.interfaces;
using network.packets;
using network.routing;
using network.utils;
using user_server.services;

namespace user_server.network;

public class GameSession : IPeer
{
    private readonly UserToken _token;
    private readonly SemaphoreSlim _sessionLock;
    private readonly ILogger _logger;
    private readonly ICacheHelper _cacheHelper;
    private readonly IRedLockFactory _redLock;
    private readonly INatsClient _natsClient;
    private readonly IProtocolRouter _protocolRouter;
    private readonly IProtocolRouter _subscribeRouter;
    private readonly PlayerService _playerService;
    private readonly MatchingManager _matchingManager;
    private readonly Action<long, GameSession> _onSessionRegistered;

    public long? PlayerId { get; private set; }
    public PlayerInfo? PlayerInfo { get; private set; }

    public GameSession(
        UserToken token,
        ILogger logger,
        ICacheHelper cacheHelper,
        IRedLockFactory redLock,
        INatsClient natsClient,
        PlayerService playerService,
        MatchingManager matchingManager,
        Action<long, GameSession> onSessionRegistered)
    {
        _token = token;
        _token.SetPeer(this);
        _sessionLock = new SemaphoreSlim(1);
        _logger = logger;
        _cacheHelper = cacheHelper;
        _redLock = redLock;
        _natsClient = natsClient;
        _playerService = playerService;
        _matchingManager = matchingManager;
        _onSessionRegistered = onSessionRegistered;

        _protocolRouter = new ProtocolRouter(logger);
        _subscribeRouter = new ProtocolRouter(logger);
        InitializeProtocolHandlers();
    }

    private void InitializeProtocolHandlers()
    {
        // 클라이언트 프로토콜
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_HEART_BEAT, HandleHeartBeat);
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_LOGIN, async (bytes) => await HandleMessage<C_TO_U_LOGIN>(bytes, Login));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_PLAYER_INFO, async (bytes) => await HandleMessage<C_TO_U_PLAYER_INFO>(bytes, GetPlayerInfo));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_WEAR_ITEM, async (bytes) => await HandleMessage<C_TO_U_WEAR_ITEM>(bytes, WearItem));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_USE_ITEM, async (bytes) => await HandleMessage<C_TO_U_USE_ITEM>(bytes, UseItem));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_CHAT_MSG, async (bytes) => await HandleMessage<C_TO_U_CHAT_MSG>(bytes, AppendChat));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_CHAT_LOG, async (_) => await SendChatHistory(ChatType.ALL));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_SET_NAME, async (bytes) => await HandleMessage<C_TO_U_SET_NAME>(bytes, SetName));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_QUEST_INCREASE, async (bytes) => await HandleMessage<C_TO_U_QUEST_INCREASE>(bytes, IncreaseQuestCount));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_QUEST_SUCCESS, async (bytes) => await HandleMessage<C_TO_U_QUEST_SUCCESS>(bytes, CompleteQuest));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_MAIL_LIST, async (_) => await SendMailList());
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_MAIL_RECEIVE, async (bytes) => await HandleMessage<C_TO_U_MAIL_RECEIVE>(bytes, ReceiveMail));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_MATCHING, async (bytes) => await HandleMessage<C_TO_U_MATCHING>(bytes, HandleMatching));
        _protocolRouter.RegisterHandler(Protocol.C_TO_U_MATCHING_CANCEL, async (_) => await HandleMatchingCancel());

        // 구독 프로토콜
        _subscribeRouter.RegisterHandler(Protocol.U_TO_C_CHAT_MSG, bytes => HandleMessage<U_TO_C_CHAT_MSG>(bytes, SubscribeChatMsg));
        _subscribeRouter.RegisterHandler(Protocol.U_TO_U_DUPLICATE, _ => { ReceiveDuplicate(); return Task.CompletedTask; });
    }

    public async Task OnMessageFromClient(Const<byte[]> buffer)
    {
        try
        {
            await _sessionLock.WaitAsync();

            using var packet = Packet.Create(buffer);
            var protocolId = (Protocol)packet.PopProtocolId();
            var playerId = packet.PopPlayerId();
            var body = packet.PopBody();

            if (protocolId != Protocol.C_TO_U_HEART_BEAT)
            {
                _logger.LogInformation("[Receive] Protocol: {Protocol}, PlayerId: {PlayerId}, BodyLength: {BodyLength}", protocolId, playerId, body.Length);
            }

            await _protocolRouter.RouteAsync(protocolId, body);

            _logger.LogInformation("[Processed] Protocol: {Protocol} completed", protocolId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing client message");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task OnMessageFromNatsWrapper(byte[] message)
    {
        try
        {
            using var packet = new Packet(message);
            var protocolId = (Protocol)packet.PopProtocolId();
            var body = packet.PopBody();

            await _sessionLock.WaitAsync();
            try
            {
                await _subscribeRouter.RouteAsync(protocolId, body);
            }
            finally
            {
                _sessionLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing NATS message");
        }
    }

    private async Task HandleMessage<T>(byte[] body, Func<T, Task> handler) where T : IMessagePackObject
    {
        var message = MessagePackSerializer.Deserialize<T>(body);
        await handler(message);
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
            _logger.LogInformation("Login request received: AccountToken={AccountToken}", msg.AccountToken);

            // AccountToken이 없거나 파싱 실패시 Redis INCR로 새 PlayerId 생성
            long playerId;
            if (string.IsNullOrEmpty(msg.AccountToken) || !long.TryParse(msg.AccountToken, out playerId))
            {
                // Redis INCR을 사용해 1부터 순차 증가하는 PlayerId 생성
                playerId = await _cacheHelper.StringIncrementAsync("player_id_counter");
                _logger.LogInformation("Generated new PlayerId from Redis: {PlayerId}", playerId);
            }

            PlayerId = playerId;
            _logger.LogInformation("PlayerId set to {PlayerId}", PlayerId);

            await using var playerLock = await PlayerInfo.Lock(_redLock, PlayerId.Value);
            _logger.LogInformation("Player lock acquired for PlayerId={PlayerId}", PlayerId);

            PlayerInfo = await PlayerInfo.Load(_cacheHelper, PlayerId.Value);

            if (PlayerInfo == null)
            {
                // 신규 플레이어 생성
                _logger.LogInformation("Creating new player: PlayerId={PlayerId}", PlayerId);
                PlayerInfo = new PlayerInfo(PlayerId.Value, isDummy: false);
                await PlayerInfo.Save(_cacheHelper);
                _logger.LogInformation("New player created and saved: PlayerId={PlayerId}", PlayerId);
            }
            else
            {
                _logger.LogInformation("Existing player loaded: PlayerId={PlayerId}, Name={Name}", PlayerId, PlayerInfo.Name);
            }

            // 세션 등록
            _onSessionRegistered(PlayerId.Value, this);
            _logger.LogInformation("Session registered for PlayerId={PlayerId}", PlayerId);

            // TODO: SubjectHelper에 GetDuplicateLoginSubject, GetPlayerSubject 추가 필요
            // 중복 로그인 체크 (다른 세션에 중복 알림 전송)
            // var duplicateSubject = SubjectHelper.GetDuplicateLoginSubject(PlayerId.Value);
            // _natsClient.Publish(duplicateSubject, Array.Empty<byte>());

            // NATS 구독 시작
            // var playerSubject = SubjectHelper.GetPlayerSubject(PlayerId.Value);
            // _natsClient.Subscribe(playerSubject, (subject, body) => _ = OnMessageFromNatsWrapper(body));

            // 로그인 응답 전송
            _logger.LogInformation("Creating login packet for PlayerId={PlayerId}", PlayerId);
            using var loginPacket = PacketMaker.U_TO_C_LOGIN(PlayerInfo);
            _logger.LogInformation("Sending U_TO_C_LOGIN packet for PlayerId={PlayerId}, Packet size={Size}", PlayerId, loginPacket.ToBytes().Length);
            Send(loginPacket);

            _logger.LogInformation("Player {PlayerId} logged in successfully", PlayerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed for player {AccountToken}", msg.AccountToken);
        }
    }

    private async Task GetPlayerInfo(C_TO_U_PLAYER_INFO msg)
    {
        var playerInfoList = new List<PlayerInfo>();

        foreach (var playerId in msg.PlayerIdList)
        {
            await using var playerLock = await PlayerInfo.Lock(_redLock, playerId);
            var playerInfo = await PlayerInfo.Load(_cacheHelper, playerId);

            if (playerInfo != null)
            {
                playerInfoList.Add(playerInfo);
            }
        }

        if (playerInfoList.Count > 0)
        {
            using var packet = PacketMaker.U_TO_C_PLAYER_INFO(playerInfoList);
            Send(packet);
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
    }

    private async Task IncreaseQuestCount(C_TO_U_QUEST_INCREASE msg)
    {
        if (PlayerId == null) return;

        var errorCode = await _playerService.IncreaseQuestCount(PlayerId.Value, msg);

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
    }

    private async Task SendMailList()
    {
        if (PlayerId == null) return;

        var mailDict = await _playerService.GetMailList(PlayerId.Value);

        if (mailDict != null)
        {
            using var packet = PacketMaker.U_TO_C_MAIL_LIST(mailDict, true);
            Send(packet);
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
    }

    // ========== 채팅 ==========

    private async Task AppendChat(C_TO_U_CHAT_MSG msg)
    {
        if (PlayerId == null || PlayerInfo == null) return;

        _logger.LogInformation($"Chat from {PlayerId}: {msg.ChatMessage}");

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

    private async Task SendChatHistory(ChatType chatType)
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
        _logger.LogWarning($"Duplicate login detected for player {PlayerId}");
        Disconnect();
    }

    public void Send(IPacket packet)
    {
        if (packet is Packet p)
        {
            _token.Send(p);
        }
    }

    public string GetChannelName()
    {
        // TODO: SubjectHelper에 GetPlayerSubject 추가 필요
        return $"player.{PlayerId ?? 0}";
    }

    private void Disconnect()
    {
        _token.Disconnect();
    }

    public void OnRemoved()
    {
        _logger.LogInformation($"Session removed: PlayerId={PlayerId}");
    }

    public void OnDisconnect()
    {
        _logger.LogInformation($"Session disconnected: PlayerId={PlayerId}");

        // TODO: INatsClient에 Unsubscribe 메서드가 없음 - NATS 라이브러리 확인 필요
        // if (PlayerId.HasValue)
        // {
        //     var playerSubject = SubjectHelper.GetPlayerSubject(PlayerId.Value);
        //     _natsClient.Unsubscribe(playerSubject);
        // }
    }

    public Task<UserToken?> Release()
    {
        return Task.FromResult<UserToken?>(_token);
    }
}
