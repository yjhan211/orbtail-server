using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.infrastructure.redis;
using network.packets;
using user_server.accounts;
using user_server.matching;
using user_server.players;

namespace user_server.sessions;

/// <summary>
///     UserServer에 접속한 클라이언트 한 명의 요청을 처리한다.
///     아이템 요청은 PlayerService에, 매칭 요청은 MatchingManager에 위임한다.
///     실제 TCP 송수신과 연결 종료는 TcpConnection이 담당한다.
/// </summary>
public sealed class PlayerSession : SessionBase, IMatchingSessionEndpoint
{
    private readonly IMatchingManager _matchingManager;
    private readonly IAccountTokenService _accountTokenService;
    private readonly IRedLockFactory _redLock;
    private readonly IPlayerSessionLeaseStore _sessionLeaseStore;
    private readonly IPlayerService _playerService;

    private readonly Func<long, PlayerSession, (bool Accepted, PlayerSession? PreviousSession)> _onSessionRegistered;
    private readonly Action<long, long> _announceLogin;
    private readonly Func<long, PlayerSession, bool> _onSessionRemoved;
    private readonly Func<Func<Task>, string, bool> _tryRunBackgroundOperation;

    private readonly string _nodeId;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    private readonly MatchingAssignment _matchingAssignment = new();
    private PlayerSessionLease? _sessionLease;
    private long _sessionGeneration;

    public PlayerSession(
        TcpConnection connection,
        ILogger logger,
        IRedisOperations redisOperations,
        IRedLockFactory redLock,
        IPlayerService playerService,
        IMatchingManager matchingManager,
        IAccountTokenService accountTokenService,
        IPlayerSessionLeaseStore sessionLeaseStore,
        string nodeId,
        Func<long, PlayerSession, (bool Accepted, PlayerSession? PreviousSession)> onSessionRegistered,
        Action<long, long> announceLogin,
        Func<long, PlayerSession, bool> onSessionRemoved,
        Func<Func<Task>, string, bool> tryRunBackgroundOperation)
        : base(connection, logger, redisOperations)
    {
        _playerService = playerService;
        _matchingManager = matchingManager;
        _accountTokenService = accountTokenService;
        _redLock = redLock;
        _sessionLeaseStore = sessionLeaseStore;
        _nodeId = string.IsNullOrWhiteSpace(nodeId)
            ? throw new ArgumentException("UserServer node id is required.", nameof(nodeId))
            : nodeId;
        _onSessionRegistered = onSessionRegistered;
        _announceLogin = announceLogin;
        _onSessionRemoved = onSessionRemoved;
        _tryRunBackgroundOperation = tryRunBackgroundOperation;

        InitializeProtocolHandlers();
    }

    private new long? PlayerId { get; set; }
    private PlayerInfo? PlayerInfo { get; set; }
    internal long SessionGeneration => Volatile.Read(ref _sessionGeneration);
    internal string? ActiveMatchingRequestId => _matchingAssignment.ActiveRequestId;

    protected override async Task<bool> CanProcessMessageAsync(Protocol protocolId)
    {
        if (protocolId == Protocol.C_TO_U_LOGIN) return true;

        var lease = _sessionLease;
        if (Connection.IsReleased) return false;
        if (lease == null) return protocolId == Protocol.C_TO_U_HEART_BEAT;
        // 로그인 이후의 패킷(하트비트 포함)으로 현재 세션 검사와 TTL 갱신을 함께 수행한다.
        if (await _sessionLeaseStore.TryRenewAsync(lease)) return !Connection.IsReleased;

        SendErrorResponseAndDisconnect(ErrorCode.ALREADY_CONNECTED);
        return false;
    }

    protected override void InitializeProtocolHandlers()
    {
        // 클라이언트 프로토콜
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_HEART_BEAT, HandleHeartBeat);
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_LOGIN,
            async bytes => await HandleMessage<C_TO_U_LOGIN>(bytes, Login));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_WEAR_ITEM,
            async bytes => await HandleMessage<C_TO_U_WEAR_ITEM>(bytes, WearItem));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_USE_ITEM,
            async bytes => await HandleMessage<C_TO_U_USE_ITEM>(bytes, UseItem));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_MATCHING,
            async bytes => await HandleMessage<C_TO_U_MATCHING>(bytes, HandleMatching));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_U_MATCHING_CANCEL, async _ => await HandleMatchingCancel());
    }

    protected override bool ShouldSkipLogging(Protocol protocolId)
    {
        return protocolId == Protocol.C_TO_U_HEART_BEAT;
    }

    private Task HandleHeartBeat(byte[] _)
    {
        using var packet = PacketMaker.U_TO_C_HEART_BEAT(DateTime.UtcNow);
        TrySend(packet);
        return Task.CompletedTask;
    }

    private async Task Login(C_TO_U_LOGIN msg)
    {
        if (PlayerId.HasValue || PlayerInfo != null)
        {
            Logger.LogWarning("Repeated login attempt on an authenticated session: PlayerId={PlayerId}", PlayerId);
            SendErrorResponseAndDisconnect(ErrorCode.ALREADY_AUTHENTICATED);
            return;
        }

        try
        {
            Logger.LogInformation("Login request received: HasAccountCredential={HasAccountCredential}", !string.IsNullOrWhiteSpace(msg.AccountToken));
            (long playerId, string accountToken, bool isNewAccount) = await _accountTokenService.ResolveAsync(msg.AccountToken);
            PlayerId = playerId;

            await LoadOrCreatePlayerAsync(isNewAccount);

            var lease = await TryAcquireSessionLeaseAsync();
            if (lease == null)
            {
                return;
            }
            if (!TryRegisterLocalSession(out var previousSession))
            {
                return;
            }
            previousSession?.DisconnectForDuplicateLogin();

            _announceLogin(PlayerId.Value, SessionGeneration);

            Logger.LogInformation("Session registered for PlayerId={PlayerId}, Generation={Generation}", PlayerId, SessionGeneration);
            SendLoginResponse(accountToken);
        }
        catch (AccountAuthenticationException ex)
        {
            Logger.LogWarning(ex, "Login authentication failed");
            SendErrorResponseAndDisconnect(ErrorCode.AUTH_FAILED);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Login failed");
            SendErrorResponseAndDisconnect(ErrorCode.SERVER_INTERNAL_ERROR);
        }
    }

    private async Task LoadOrCreatePlayerAsync(bool isNewAccount)
    {
        await using var playerLock = await PlayerInfo.Lock(_redLock, PlayerId!.Value);

        PlayerInfo = await PlayerInfo.Load(RedisOperations, PlayerId.Value);
        if (PlayerInfo == null)
        {
            if (!isNewAccount)
                Logger.LogWarning("Recovering a provisioned account with missing PlayerInfo: PlayerId={PlayerId}", PlayerId);

            PlayerInfo = new PlayerInfo(PlayerId.Value, false);
            await PlayerInfo.Save(RedisOperations);
            Logger.LogInformation("New player created: PlayerId={PlayerId}", PlayerId);
        }

        if (PlayerInfo.IsNew) await SetupNewPlayer(PlayerInfo);
    }

    /// <summary>
    ///     이 세션을 Redis에 플레이어의 현재 세션으로 등록한다.
    ///     더 높은 세대 번호의 로그인이 이미 소유권을 얻었다면, 이 연결을 끊고 null을 반환한다.
    /// </summary>
    private async Task<PlayerSessionLease?> TryAcquireSessionLeaseAsync()
    {
        var lease = await _sessionLeaseStore.TryAcquireAsync(PlayerId!.Value, _nodeId, _sessionId);
        if (lease == null)
        {
            Logger.LogWarning("Login lost session lease before entry: PlayerId={PlayerId}", PlayerId);
            SendErrorResponseAndDisconnect(ErrorCode.ALREADY_CONNECTED);
            return null;
        }

        _sessionLease = lease;
        Volatile.Write(ref _sessionGeneration, lease.Generation);
        return lease;
    }

    /// <summary>
    ///     연결을 인증 완료 상태로 바꾸면서, 이 UserServer의 접속자 목록에 세션을 등록한다.
    ///     같은 서버에 더 최신 세션이 이미 등록돼 있으면 이 연결을 끊고 false를 반환한다.
    ///     교체된 이전 세션은 previousSession로 반환하며, 호출자가 연결을 끊는다.
    /// </summary>
    private bool TryRegisterLocalSession(out PlayerSession? previousSession)
    {
        (bool Accepted, PlayerSession? PreviousSession) registration = default;
        if (!Connection.TryMarkAuthenticated(() => registration = _onSessionRegistered(PlayerId!.Value, this)))
        {
            previousSession = null;
            Logger.LogDebug("Connection closed before login entry: PlayerId={PlayerId}", PlayerId);
            return false;
        }

        previousSession = registration.PreviousSession;
        if (registration.Accepted)
        {
            return true;
        }

        Logger.LogWarning("Stale login rejected by the local session registry: PlayerId={PlayerId}, Generation={Generation}", PlayerId, SessionGeneration);
        Connection.Disconnect();
        return false;
    }

    private void SendLoginResponse(string accountToken)
    {
        using var loginPacket = PacketMaker.U_TO_C_LOGIN(PlayerInfo!, accountToken);
        TrySend(loginPacket);

        if (PlayerInfo!.InventoryInfo.ItemDict.Count > 0)
        {
            using var itemListPacket = PacketMaker.U_TO_C_INVENTORY_ITEM_LIST(new Dictionary<long, ItemInfo>(PlayerInfo.InventoryInfo.ItemDict));
            TrySend(itemListPacket);
        }
    }

    private async Task SetupNewPlayer(PlayerInfo playerInfo)
    {
        try
        {
            Logger.LogInformation("Setting up new player: PlayerId={PlayerId}", playerInfo.PlayerId);

            int[] defaultItemIds = [101000003, 102000003, 104000005, 105000005, 106000003];
            var additionalEquipmentIds = GameItemData.GetAllList()
                .Where(item => item.IsEquipment)
                .Where(item => !defaultItemIds.Contains(item.Id))
                .Where(item => GameItemData.GetEquipType(item.Id) is
                    EquipType.HEAD or EquipType.FACE or EquipType.HAT or EquipType.TOP or EquipType.BOTTOM
                    or EquipType.SHOES)
                .Select(item => item.Id)
                .Distinct()
                .ToList();

            var existingItemIds = playerInfo.InventoryInfo.ItemDict.Values
                .Select(item => item.ItemId)
                .ToHashSet();
            var missingItemIds = defaultItemIds
                .Concat(additionalEquipmentIds)
                .Where(itemId => !existingItemIds.Contains(itemId))
                .ToList();

            long itemUidUpperBound = 0;
            long itemUidCounter = 0;
            if (missingItemIds.Count > 0)
            {
                itemUidUpperBound = await RedisOperations.StringIncrementByAsync(
                    "item_uid_counter",
                    missingItemIds.Count);
                itemUidCounter = checked(itemUidUpperBound - missingItemIds.Count + 1);
                if (itemUidCounter <= 0)
                    throw new InvalidOperationException("The item UID counter produced an invalid range.");
            }

            ItemInfo EnsureItem(int itemId)
            {
                var existingItem = playerInfo.InventoryInfo.ItemDict.Values
                    .FirstOrDefault(item => item.ItemId == itemId);
                if (existingItem != null) return existingItem;

                var item = new ItemInfo(itemUidCounter++, itemId, 1);
                playerInfo.InventoryInfo.ItemDict.Add(item.ItemUid, item);
                return item;
            }

            // 기본 아이템 5종 먼저 추가 (Hair, Face, Top, Bottom, Shoes)
            var defaultHair = EnsureItem(defaultItemIds[0]);
            var defaultFace = EnsureItem(defaultItemIds[1]);
            var defaultTop = EnsureItem(defaultItemIds[2]);
            var defaultBottom = EnsureItem(defaultItemIds[3]);
            var defaultShoes = EnsureItem(defaultItemIds[4]);

            Logger.LogInformation("Added default items: Hair={HairUid}, Face={FaceUid}, Top={TopUid}, Bottom={BottomUid}, Shoes={ShoesUid}",
                defaultHair.ItemUid, defaultFace.ItemUid, defaultTop.ItemUid, defaultBottom.ItemUid, defaultShoes.ItemUid);

            // 기본 아이템 착용 (직접 처리)
            defaultHair.IsWear = true;
            defaultFace.IsWear = true;
            defaultTop.IsWear = true;
            defaultBottom.IsWear = true;
            defaultShoes.IsWear = true;

            foreach (int defaultItemId in defaultItemIds)
                if (!playerInfo.WearItemIdList.Contains(defaultItemId))
                    playerInfo.WearItemIdList.Add(defaultItemId);

            Logger.LogInformation("Default items equipped for PlayerId={PlayerId}, WearItemCount={Count}",
                playerInfo.PlayerId, playerInfo.WearItemIdList.Count);

            // 테스트용 코스튬 아이템 전부 지급
            foreach (int itemId in additionalEquipmentIds)
                EnsureItem(itemId);

            if (missingItemIds.Count > 0 && itemUidCounter != checked(itemUidUpperBound + 1))
                throw new InvalidOperationException("The reserved item UID range was not consumed exactly.");

            // 저장
            playerInfo.IsNew = false;
            await playerInfo.Save(RedisOperations);

            Logger.LogInformation("New player setup complete: PlayerId={PlayerId}, Total items={Count}",
                playerInfo.PlayerId, playerInfo.InventoryInfo.ItemDict.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to setup new player: PlayerId={PlayerId}", playerInfo.PlayerId);
            throw;
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
            TrySend(packet);
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
            TrySend(packet);
        }
        else
        {
            SendErrorResponse(errorCode, "아이템 사용 실패");
        }
    }

    // ========== 매칭 ==========

    private async Task HandleMatching(C_TO_U_MATCHING msg)
    {
        if (PlayerId == null) return;

        string? requestId = await TryBeginMatchingRequestAsync(PlayerId.Value);
        if (requestId == null)
        {
            using var alreadyMatchingPacket = PacketMaker.U_TO_C_MATCHING(ErrorCode.MATCHING_ALREADY_IN_QUEUE);
            TrySend(alreadyMatchingPacket);
            return;
        }

        ErrorCode errorCode;
        try
        {
            errorCode = await _matchingManager.AddToQueue(PlayerId.Value, this);
        }
        catch
        {
            _matchingAssignment.ClearRequest(requestId);
            throw;
        }
        if (errorCode != ErrorCode.SUCCESS)
            _matchingAssignment.ClearRequest(requestId);

        using var packet = PacketMaker.U_TO_C_MATCHING(errorCode);
        TrySend(packet);
    }

    internal async Task<string?> TryBeginMatchingRequestAsync(long playerId)
    {
        if (Connection.IsReleased)
        {
            return null;
        }

        if (_matchingAssignment.TryBegin(out string? requestId, out long assignedMatchingId))
        {
            return requestId;
        }

        if (assignedMatchingId == 0)
        {
            return null;
        }

        if (await _matchingManager.HasReservationAsync(playerId))
        {
            return null;
        }

        if (Connection.IsReleased)
        {
            return null;
        }
        requestId = _matchingAssignment.TryRestart(assignedMatchingId);
        if (requestId == null)
        {
            return null;
        }

        Logger.LogInformation("Cleared stale local matching assignment after Redis reservation disappeared: PlayerId={PlayerId}, MatchingId={MatchingId}",
            playerId, assignedMatchingId);

        return requestId;
    }

    private async Task HandleMatchingCancel()
    {
        if (PlayerId == null) return;

        string? requestId = ActiveMatchingRequestId;
        var errorCode = await _matchingManager.CancelMatching(PlayerId.Value);
        if (errorCode == ErrorCode.SUCCESS && requestId != null)
        {
            _matchingAssignment.ClearRequest(requestId);
        }

        using var packet = PacketMaker.U_TO_C_MATCHING_CANCEL(errorCode);
        TrySend(packet);
    }

    bool IMatchingSessionEndpoint.TryDeliverMatchingSuccess(long matchingId, string requestId, Packet packet)
    {
        if (Connection.IsReleased || !_matchingAssignment.TryAssign(matchingId, requestId))
        {
            return false;
        }

        if (TrySend(packet))
        {
            return true;
        }

        _matchingAssignment.Clear(matchingId);
        return false;
    }

    bool IMatchingSessionEndpoint.TryDeliverMatchingFailed(long matchingId, string requestId, Packet packet)
    {
        if (!_matchingAssignment.FailRequest(requestId, matchingId))
        {
            return false;
        }
        return TrySend(packet);
    }

    bool IMatchingSessionEndpoint.TryDeliverEntryFailed(long matchingId, Packet packet)
    {
        if (!_matchingAssignment.FailEntry(matchingId))
        {
            return true;
        }
        return TrySend(packet);
    }

    void IMatchingSessionEndpoint.ClearMatchingAssignment(long matchingId) => _matchingAssignment.Clear(matchingId);

    // ========== 기타 ==========

    public override bool TrySend(Packet packet)
    {
        try
        {
            bool sent = Connection.TrySend(packet);
            if (sent && packet.ProtocolId != (int)Protocol.U_TO_C_HEART_BEAT)
                Logger.LogInformation("Packet sent: Protocol={Protocol}, PlayerId={PlayerId}", (Protocol)packet.ProtocolId, PlayerId);
            return sent;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send packet: PlayerId={PlayerId}", PlayerId);
            return false;
        }
    }

    private void DisconnectForDuplicateLogin()
    {
        Logger.LogWarning($"Duplicate login detected for player {PlayerId}");
        Connection.Disconnect();
    }

    public void DisconnectIfOlderSession(long newGeneration)
    {
        if (newGeneration <= SessionGeneration)
            return;

        Logger.LogWarning("Session superseded by a newer login: PlayerId={PlayerId}, Generation={Generation}, NewGeneration={NewGeneration}",
            PlayerId, SessionGeneration, newGeneration);
        Connection.Disconnect();
    }

    protected override void SendErrorResponse(ErrorCode errorCode, string message)
    {
        if (!string.IsNullOrEmpty(message))
            Logger.LogWarning("Request failed: PlayerId={PlayerId}, ErrorCode={ErrorCode}, Detail={Detail}", PlayerId, errorCode, message);
        try
        {
            using var packet = PacketMaker.U_TO_C_ERROR(errorCode);
            TrySend(packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "에러 응답 전송 실패: PlayerId={PlayerId}", PlayerId);
        }
    }

    private void SendErrorResponseAndDisconnect(ErrorCode errorCode)
    {
        try
        {
            using var packet = PacketMaker.U_TO_C_ERROR(errorCode);
            Connection.TrySendAndDisconnect(packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send final error response: PlayerId={PlayerId}", PlayerId);
            Connection.Disconnect();
        }
    }


    public override void OnRemoved()
    {
        Logger.LogInformation("Session removed: PlayerId={PlayerId}", PlayerId);

        if (!PlayerId.HasValue)
            return;

        long playerId = PlayerId.Value;
        bool removedLocally = _onSessionRemoved(playerId, this);
        if (_sessionLease == null && !removedLocally)
            return;

        _tryRunBackgroundOperation(
            () => CleanupRemovedSessionAsync(playerId, removedLocally),
            $"cleanup disconnected player {playerId}");
    }

    private async Task CleanupRemovedSessionAsync(long playerId, bool cleanupMatching)
    {

        var lease = Interlocked.Exchange(ref _sessionLease, null);
        if (lease != null)
        {
            try
            {
                bool released = await _sessionLeaseStore.TryReleaseAsync(lease);
                Logger.LogInformation("Session lease release completed: PlayerId={PlayerId}, Generation={Generation}, Released={Released}",
                    lease.PlayerId, lease.Generation, released);
            }
            catch (Exception ex)
            {
                // Redis 등록은 TTL로 만료된다. 해제 실패와 무관하게 매칭 정리도 시도한다.
                Logger.LogWarning(ex, "Session lease release failed; continuing matching cleanup: PlayerId={PlayerId}", playerId);
            }
        }

        if (!cleanupMatching)
            return;

        long matchingId = _matchingAssignment.TakeAndClear();
        if (matchingId > 0)
            await _matchingManager.ReleaseMatchingReservationAsync(playerId, matchingId);
        else
            await _matchingManager.CancelMatching(playerId);
    }

    public override void OnDisconnect()
    {
        Logger.LogInformation("Session disconnected: PlayerId={PlayerId}", PlayerId);
    }
}
