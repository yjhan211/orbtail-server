using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.gamehandoff;
using network.interfaces;
using network.packets;
using user_server.services;

namespace user_server.network;

public sealed class GameSession : SessionBase, IMatchingSessionEndpoint
{
    private readonly IMatchingManager _matchingManager;
    private readonly IAccountTokenService _accountTokenService;
    private readonly IRedLockFactory _redLock;
    private readonly IPlayerSessionOwnershipStore _sessionOwnership;
    private readonly string _nodeId;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private PlayerSessionLease? _sessionLease;
    private CancellationTokenSource? _ownershipRenewalCts;
    private Task _ownershipRenewalTask = Task.CompletedTask;
    private long _sessionGeneration;
    private readonly Func<long, GameSession, (bool Accepted, Action? DisconnectSuperseded)> _onSessionRegistered;
    private readonly Action<long, long> _announceLogin;
    private readonly Func<long, GameSession, bool> _onSessionRemoved;
    private readonly IPlayerService _playerService;
    private readonly object _matchingAssignmentLock = new();
    private string? _activeMatchingRequestId;
    private long _assignedMatchingId;

    public GameSession(
        UserToken token,
        ILogger logger,
        IRedisOperations redisOperations,
        IRedLockFactory redLock,
        IPlayerService playerService,
        IMatchingManager matchingManager,
        IAccountTokenService accountTokenService,
        IPlayerSessionOwnershipStore sessionOwnership,
        string nodeId,
        Func<long, GameSession, (bool Accepted, Action? DisconnectSuperseded)> onSessionRegistered,
        Action<long, long> announceLogin,
        Func<long, GameSession, bool> onSessionRemoved)
        : base(token, logger, redisOperations)
    {
        _playerService = playerService;
        _matchingManager = matchingManager;
        _accountTokenService = accountTokenService;
        _redLock = redLock;
        _sessionOwnership = sessionOwnership;
        _nodeId = string.IsNullOrWhiteSpace(nodeId)
            ? throw new ArgumentException("UserServer node id is required.", nameof(nodeId))
            : nodeId;
        _onSessionRegistered = onSessionRegistered;
        _announceLogin = announceLogin;
        _onSessionRemoved = onSessionRemoved;

        // ReSharper disable once VirtualMemberCallInConstructor
        InitializeProtocolHandlers();
    }

    public new long? PlayerId { get; private set; }
    public PlayerInfo? PlayerInfo { get; private set; }
    internal long SessionGeneration => Volatile.Read(ref _sessionGeneration);
    long IMatchingSessionEndpoint.SessionGeneration => SessionGeneration;
    internal string? ActiveMatchingRequestId
    {
        get
        {
            lock (_matchingAssignmentLock)
                return _activeMatchingRequestId;
        }
    }
    internal bool IsConnected => !Token.IsReleased;

    internal bool TryAssignMatching(long matchingId, string requestId)
    {
        if (matchingId <= 0 ||
            !MatchingRequestTokens.IsSafeTokenComponent(requestId))
            return false;

        lock (_matchingAssignmentLock)
        {
            if (!IsConnected ||
                !string.Equals(_activeMatchingRequestId, requestId, StringComparison.Ordinal) ||
                (_assignedMatchingId != 0 && _assignedMatchingId != matchingId))
                return false;

            _assignedMatchingId = matchingId;
            return true;
        }
    }

    internal void ClearMatchingAssignment(long matchingId)
    {
        if (matchingId <= 0)
            return;

        lock (_matchingAssignmentLock)
        {
            if (_assignedMatchingId == matchingId)
            {
                _assignedMatchingId = 0;
                _activeMatchingRequestId = null;
            }
        }
    }

    private long TakeMatchingAssignment()
    {
        lock (_matchingAssignmentLock)
        {
            long matchingId = _assignedMatchingId;
            _assignedMatchingId = 0;
            _activeMatchingRequestId = null;
            return matchingId;
        }
    }

    /// <summary>
    ///     새 매칭 요청 fence를 만든다. NATS의 session.clear가 유실돼 로컬 배정만 남았으면 Redis claim을
    ///     다시 확인하고, claim이 완전히 사라진 경우에만 stale 배정을 지운 뒤 같은 요청을 계속한다.
    /// </summary>
    internal async Task<string?> TryBeginMatchingRequestAsync(long playerId)
    {
        long assignedMatchingId;
        lock (_matchingAssignmentLock)
        {
            if (!IsConnected)
                return null;

            assignedMatchingId = _assignedMatchingId;
            if (assignedMatchingId == 0)
            {
                if (_activeMatchingRequestId != null)
                    return null;

                _activeMatchingRequestId = Guid.NewGuid().ToString("N");
                return _activeMatchingRequestId;
            }
        }

        // claim 값이 다른 경우도 다른 매칭 작업이 진행 중인 것으로 보고 보수적으로 배정을 유지한다.
        if (await _matchingManager.HasMatchingClaimAsync(playerId))
            return null;

        string? requestId;
        lock (_matchingAssignmentLock)
        {
            if (!IsConnected)
                return null;

            // Redis를 기다리는 동안 더 새로운 배정이 생겼으면 그 상태를 건드리지 않는다.
            if (_assignedMatchingId != 0 && _assignedMatchingId != assignedMatchingId)
                return null;

            if (_assignedMatchingId == assignedMatchingId)
            {
                _assignedMatchingId = 0;
                _activeMatchingRequestId = null;
            }

            // clear 알림 등 다른 경로가 먼저 정리했어도 새 요청이 이미 시작됐다면 끼어들지 않는다.
            if (_assignedMatchingId != 0 || _activeMatchingRequestId != null)
                return null;

            requestId = Guid.NewGuid().ToString("N");
            _activeMatchingRequestId = requestId;
        }

        Logger.LogInformation(
            "Cleared stale local matching assignment after Redis claim disappeared: PlayerId={PlayerId}, MatchingId={MatchingId}",
            playerId,
            assignedMatchingId);
        return requestId;
    }

    private bool TryClearMatchingRequest(string requestId)
    {
        lock (_matchingAssignmentLock)
        {
            if (!string.Equals(_activeMatchingRequestId, requestId, StringComparison.Ordinal))
                return false;

            _activeMatchingRequestId = null;
            return true;
        }
    }

    private bool TryFailMatchingRequest(string requestId, long matchingId)
    {
        lock (_matchingAssignmentLock)
        {
            if (!string.Equals(_activeMatchingRequestId, requestId, StringComparison.Ordinal) ||
                (_assignedMatchingId != 0 && _assignedMatchingId != matchingId))
            {
                return false;
            }

            _activeMatchingRequestId = null;
            if (_assignedMatchingId == matchingId)
                _assignedMatchingId = 0;
            return true;
        }
    }

    private bool TryFailMatchingAdmission(long matchingId)
    {
        lock (_matchingAssignmentLock)
        {
            if (_assignedMatchingId != matchingId)
                return false;

            _assignedMatchingId = 0;
            _activeMatchingRequestId = null;
            return true;
        }
    }

    internal bool TrySend(Packet packet)
    {
        try
        {
            bool sent = Token.TrySend(packet);
            if (sent && packet.ProtocolId != (int)Protocol.U_TO_C_HEART_BEAT)
                Logger.LogInformation("Packet sent: Protocol={Protocol}, PlayerId={PlayerId}",
                    (Protocol)packet.ProtocolId, PlayerId);
            return sent;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send packet: PlayerId={PlayerId}", PlayerId);
            return false;
        }
    }

    /// <summary>
    ///     매칭 성공 패킷을 보낸다. 요청 ID가 현재 매칭 요청과 다르거나 전송에 실패하면 배정을 남기지 않는다.
    /// </summary>
    internal bool TryDeliverMatchingSuccess(long matchingId, string requestId, Packet packet)
    {
        if (!TryAssignMatching(matchingId, requestId))
            return false;
        if (TrySend(packet))
            return true;

        ClearMatchingAssignment(matchingId);
        return false;
    }

    /// <summary>
    ///     매칭 롤백 실패 패킷을 보낸다. 요청 ID가 현재 매칭 요청과 다르면 보내지 않는다.
    /// </summary>
    internal bool TryDeliverMatchingFailed(long matchingId, string requestId, Packet packet)
    {
        if (!TryFailMatchingRequest(requestId, matchingId))
            return false;
        return TrySend(packet);
    }

    /// <summary>
    ///     입장 실패 패킷을 보낸다. 이 세션이 해당 매치에 배정돼 있지 않으면 보낼 것이 없으므로 성공으로 본다.
    /// </summary>
    internal bool TryDeliverAdmissionFailed(long matchingId, Packet packet)
    {
        if (!TryFailMatchingAdmission(matchingId))
            return true;
        return TrySend(packet);
    }

    // 라우터 port — 세션이 다른 User Server에 있을 때 같은 fence 규칙으로 대신 처리하게 한다.
    bool IMatchingSessionEndpoint.TryDeliverMatchingSuccess(long matchingId, string requestId, Packet packet) =>
        TryDeliverMatchingSuccess(matchingId, requestId, packet);

    bool IMatchingSessionEndpoint.TryDeliverMatchingFailed(long matchingId, string requestId, Packet packet) =>
        TryDeliverMatchingFailed(matchingId, requestId, packet);

    bool IMatchingSessionEndpoint.TryDeliverAdmissionFailed(long matchingId, Packet packet) =>
        TryDeliverAdmissionFailed(matchingId, packet);

    void IMatchingSessionEndpoint.ClearMatchingAssignment(long matchingId) => ClearMatchingAssignment(matchingId);

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
        Send(packet);
        return Task.CompletedTask;
    }

    private async Task Login(C_TO_U_LOGIN msg)
    {
        if (PlayerId.HasValue || PlayerInfo != null)
        {
            Logger.LogWarning("Repeated login attempt on an authenticated session: PlayerId={PlayerId}", PlayerId);
            SendErrorResponseAndDisconnect(ErrorCode.AUTH_FAILED, "이미 인증된 세션입니다");
            return;
        }

        try
        {
            Logger.LogInformation("Login request received: HasAccountCredential={HasAccountCredential}",
                !string.IsNullOrWhiteSpace(msg.AccountToken));

            AccountTokenResolution account = await _accountTokenService.ResolveAsync(msg.AccountToken);
            long playerId = account.PlayerId;

            PlayerId = playerId;
            Logger.LogInformation("PlayerId set to {PlayerId}", PlayerId);

            {
                await using var playerLock = await PlayerInfo.Lock(_redLock, PlayerId.Value);
                Logger.LogInformation("Player lock acquired for PlayerId={PlayerId}", PlayerId);

                PlayerInfo = await PlayerInfo.Load(RedisOperations, PlayerId.Value);

                if (PlayerInfo == null)
                {
                    if (!account.IsNewAccount)
                    {
                        Logger.LogWarning(
                            "Recovering a provisioned account with missing PlayerInfo: PlayerId={PlayerId}",
                            PlayerId);
                    }

                    // 신규 플레이어 생성
                    Logger.LogInformation("Creating new player: PlayerId={PlayerId}", PlayerId);
                    PlayerInfo = new PlayerInfo(PlayerId.Value, false);
                    await PlayerInfo.Save(RedisOperations);
                    Logger.LogInformation("New player created and saved: PlayerId={PlayerId}", PlayerId);
                }
                else
                {
                    Logger.LogInformation("Existing player loaded: PlayerId={PlayerId}, Name={Name}", PlayerId,
                        PlayerInfo.Name);
                }

                // 신규 플레이어 초기 아이템 지급
                if (PlayerInfo.IsNew) await SetupNewPlayer(PlayerInfo);
            }

            PlayerSessionLease? sessionLease = await _sessionOwnership.TryAcquireAsync(
                PlayerId.Value,
                _nodeId,
                _sessionId);
            if (sessionLease == null)
            {
                Logger.LogWarning(
                    "Login lost session ownership before admission: PlayerId={PlayerId}",
                    PlayerId);
                SendErrorResponseAndDisconnect(ErrorCode.ALREADY_CONNECTED, "더 최근에 로그인한 세션이 있습니다");
                return;
            }

            _sessionLease = sessionLease;
            Volatile.Write(ref _sessionGeneration, sessionLease.Generation);

            // 로그인 응답 전송
            Logger.LogInformation("Creating login packet for PlayerId={PlayerId}", PlayerId);
            using var loginPacket = PacketMaker.U_TO_C_LOGIN(PlayerInfo, account.AccountToken);
            Logger.LogInformation("Sending U_TO_C_LOGIN packet for PlayerId={PlayerId}, Packet size={Size}", PlayerId,
                loginPacket.ToBytes().Length);
            (bool Accepted, Action? DisconnectSuperseded) registration = default;
            if (!Token.TryMarkAuthenticated(
                    () => registration = _onSessionRegistered(PlayerId.Value, this)))
                throw new OperationCanceledException("Connection closed before login admission.");
            if (!registration.Accepted)
            {
                Logger.LogWarning(
                    "Stale login rejected by the local session registry: PlayerId={PlayerId}, Generation={Generation}",
                    PlayerId, SessionGeneration);
                Disconnect();
                return;
            }

            registration.DisconnectSuperseded?.Invoke();
            StartOwnershipRenewal(sessionLease);
            _announceLogin(PlayerId.Value, SessionGeneration);
            Logger.LogInformation(
                "Session registered for PlayerId={PlayerId}, Generation={Generation}",
                PlayerId, SessionGeneration);
            Send(loginPacket);

            // 인벤토리 아이템 리스트 전송
            if (PlayerInfo.InventoryInfo.ItemDict.Count > 0)
            {
                using var itemListPacket = PacketMaker.U_TO_C_INVENTORY_ITEM_LIST(
                    new Dictionary<long, ItemInfo>(PlayerInfo.InventoryInfo.ItemDict));
                Send(itemListPacket);

                Logger.LogInformation(
                    "Sent inventory item list: PlayerId={PlayerId}, ItemCount={Count}",
                    PlayerId, PlayerInfo.InventoryInfo.ItemDict.Count);
            }

            Logger.LogInformation("Player {PlayerId} logged in successfully", PlayerId);
        }
        catch (AccountAuthenticationException ex)
        {
            Logger.LogWarning(ex, "Login authentication failed");
            SendErrorResponseAndDisconnect(ErrorCode.AUTH_FAILED, "계정 인증에 실패했습니다");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Login failed");
            SendErrorResponseAndDisconnect(ErrorCode.SERVER_INTERNAL_ERROR, "로그인 처리 중 오류가 발생했습니다");
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

    // ========== 매칭 ==========

    private async Task HandleMatching(C_TO_U_MATCHING msg)
    {
        if (PlayerId == null) return;

        string? requestId = await TryBeginMatchingRequestAsync(PlayerId.Value);
        if (requestId == null)
        {
            using var alreadyMatchingPacket =
                PacketMaker.U_TO_C_MATCHING(ErrorCode.MATCHING_ALREADY_IN_QUEUE);
            Send(alreadyMatchingPacket);
            return;
        }

        ErrorCode errorCode;
        try
        {
            errorCode = await _matchingManager.AddToQueue(PlayerId.Value, this);
        }
        catch
        {
            TryClearMatchingRequest(requestId);
            throw;
        }
        if (errorCode != ErrorCode.SUCCESS)
            TryClearMatchingRequest(requestId);

        using var packet = PacketMaker.U_TO_C_MATCHING(errorCode);
        Send(packet);
    }

    private async Task HandleMatchingCancel()
    {
        if (PlayerId == null) return;

        string? requestId = ActiveMatchingRequestId;
        var errorCode = await _matchingManager.CancelMatching(PlayerId.Value);
        if (errorCode == ErrorCode.SUCCESS && requestId != null)
            TryClearMatchingRequest(requestId);

        using var packet = PacketMaker.U_TO_C_MATCHING_CANCEL(errorCode);
        Send(packet);
    }

    // ========== 기타 ==========

    private void ReceiveDuplicate()
    {
        Logger.LogWarning($"Duplicate login detected for player {PlayerId}");
        Disconnect();
    }

    public void DisconnectForDuplicateLogin()
    {
        ReceiveDuplicate();
    }

    public void DisconnectIfSuperseded(long newGeneration)
    {
        if (newGeneration <= SessionGeneration)
            return;

        Logger.LogWarning(
            "Session superseded by a newer login: PlayerId={PlayerId}, Generation={Generation}, NewGeneration={NewGeneration}",
            PlayerId, SessionGeneration, newGeneration);
        ReceiveDuplicate();
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

    private void SendErrorResponseAndDisconnect(ErrorCode errorCode, string message)
    {
        try
        {
            using var packet = PacketMaker.U_TO_C_ERROR(errorCode, message);
            Token.TrySendAndDisconnect(packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send final error response: PlayerId={PlayerId}", PlayerId);
            Token.Disconnect();
        }
    }

    private void StartOwnershipRenewal(PlayerSessionLease lease)
    {
        var cts = new CancellationTokenSource();
        _ownershipRenewalCts = cts;
        _ownershipRenewalTask = RenewOwnershipAsync(lease, cts.Token);
    }

    private async Task RenewOwnershipAsync(PlayerSessionLease lease, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_sessionOwnership.RenewalInterval);
        DateTimeOffset lastSuccessfulRenewal = DateTimeOffset.UtcNow;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    if (!await _sessionOwnership.TryRenewAsync(lease))
                    {
                        Logger.LogWarning(
                            "Session ownership lost: PlayerId={PlayerId}, Generation={Generation}",
                            lease.PlayerId, lease.Generation);
                        Disconnect();
                        return;
                    }

                    lastSuccessfulRenewal = DateTimeOffset.UtcNow;
                }
                catch (Exception ex)
                {
                    if (DateTimeOffset.UtcNow - lastSuccessfulRenewal >= _sessionOwnership.LeaseLifetime)
                    {
                        Logger.LogError(
                            ex,
                            "Session ownership could not be renewed before expiry; disconnecting: PlayerId={PlayerId}, Generation={Generation}",
                            lease.PlayerId, lease.Generation);
                        Disconnect();
                        return;
                    }

                    Logger.LogWarning(
                        ex,
                        "Session ownership renewal failed; retrying before expiry: PlayerId={PlayerId}, Generation={Generation}",
                        lease.PlayerId, lease.Generation);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

        bool scheduled = _matchingManager.TryRunBackgroundOperation(
            () => CleanupRemovedSessionAsync(playerId, removedLocally),
            $"cleanup disconnected player {playerId}");
        if (!scheduled)
            _ownershipRenewalCts?.Cancel();
    }

    private async Task CleanupRemovedSessionAsync(long playerId, bool cleanupMatching)
    {
        CancellationTokenSource? renewalCts = Interlocked.Exchange(ref _ownershipRenewalCts, null);
        if (renewalCts != null)
        {
            await renewalCts.CancelAsync();
            try
            {
                await _ownershipRenewalTask;
            }
            finally
            {
                renewalCts.Dispose();
            }
        }

        PlayerSessionLease? lease = Interlocked.Exchange(ref _sessionLease, null);
        if (lease != null)
        {
            bool released = await _sessionOwnership.TryReleaseAsync(lease);
            Logger.LogInformation(
                "Session ownership release completed: PlayerId={PlayerId}, Generation={Generation}, Released={Released}",
                lease.PlayerId, lease.Generation, released);
        }

        if (!cleanupMatching)
            return;

        long matchingId = TakeMatchingAssignment();
        if (matchingId > 0)
            await _matchingManager.ReleaseMatchingClaimAsync(playerId, matchingId);
        else
            await _matchingManager.CancelMatching(playerId);
    }

    public override void OnDisconnect()
    {
        Logger.LogInformation("Session disconnected: PlayerId={PlayerId}", PlayerId);
    }

}
