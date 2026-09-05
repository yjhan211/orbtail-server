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
using user_server.sessions;

namespace user_server.network;

public sealed class GameSession : SessionBase, IMatchingSessionEndpoint
{
    private readonly IMatchingManager _matchingManager;
    private readonly IAccountTokenService _accountTokenService;
    private readonly IRedLockFactory _redLock;
    private readonly IPlayerSessionOwnershipStore _sessionOwnership;
    private readonly IPlayerService _playerService;

    private readonly Func<long, GameSession, (bool Accepted, Action? DisconnectSuperseded)> _onSessionRegistered;
    private readonly Action<long, long> _announceLogin;
    private readonly Func<long, GameSession, bool> _onSessionRemoved;

    private readonly string _nodeId;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    private readonly MatchingAssignment _matching = new();
    private PlayerSessionLease? _sessionLease;
    private CancellationTokenSource? _ownershipRenewalCts;
    private Task _ownershipRenewalTask = Task.CompletedTask;
    private long _sessionGeneration;

    public GameSession(
        TcpConnection connection,
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
        : base(connection, logger, redisOperations)
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

        InitializeProtocolHandlers();
    }

    private new long? PlayerId { get; set; }
    private PlayerInfo? PlayerInfo { get; set; }
    internal long SessionGeneration => Volatile.Read(ref _sessionGeneration);
    internal string? ActiveMatchingRequestId => _matching.ActiveRequestId;

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
            SendErrorResponseAndDisconnect(ErrorCode.AUTH_FAILED, "이미 인증된 세션입니다");
            return;
        }

        try
        {
            Logger.LogInformation("Login request received: HasAccountCredential={HasAccountCredential}",
                !string.IsNullOrWhiteSpace(msg.AccountToken));

            (long playerId, string accountToken, bool isNewAccount, _) = await _accountTokenService.ResolveAsync(msg.AccountToken);

            PlayerId = playerId;
            Logger.LogInformation("PlayerId set to {PlayerId}", PlayerId);

            {
                await using var playerLock = await PlayerInfo.Lock(_redLock, PlayerId.Value);
                Logger.LogInformation("Player lock acquired for PlayerId={PlayerId}", PlayerId);

                PlayerInfo = await PlayerInfo.Load(RedisOperations, PlayerId.Value);

                if (PlayerInfo == null)
                {
                    if (!isNewAccount)
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

            var sessionLease = await _sessionOwnership.TryAcquireAsync(
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
            using var loginPacket = PacketMaker.U_TO_C_LOGIN(PlayerInfo, accountToken);
            Logger.LogInformation("Sending U_TO_C_LOGIN packet for PlayerId={PlayerId}, Packet size={Size}", PlayerId,
                loginPacket.ToBytes().Length);
            (bool Accepted, Action? DisconnectSuperseded) registration = default;
            if (!Connection.TryMarkAuthenticated(
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
            TrySend(loginPacket);

            // 인벤토리 아이템 리스트 전송
            if (PlayerInfo.InventoryInfo.ItemDict.Count > 0)
            {
                using var itemListPacket = PacketMaker.U_TO_C_INVENTORY_ITEM_LIST(
                    new Dictionary<long, ItemInfo>(PlayerInfo.InventoryInfo.ItemDict));
                TrySend(itemListPacket);

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
            using var alreadyMatchingPacket =
                PacketMaker.U_TO_C_MATCHING(ErrorCode.MATCHING_ALREADY_IN_QUEUE);
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
            _matching.ClearRequest(requestId);
            throw;
        }
        if (errorCode != ErrorCode.SUCCESS)
            _matching.ClearRequest(requestId);

        using var packet = PacketMaker.U_TO_C_MATCHING(errorCode);
        TrySend(packet);
    }

    internal async Task<string?> TryBeginMatchingRequestAsync(long playerId)
    {
        if (Connection.IsReleased)
            return null;
        if (_matching.TryBegin(out string? requestId, out long assignedMatchingId))
            return requestId;
        if (assignedMatchingId == 0)
            return null;

        if (await _matchingManager.HasMatchingClaimAsync(playerId))
            return null;

        if (Connection.IsReleased)
            return null;
        requestId = _matching.TryReplaceStale(assignedMatchingId);
        if (requestId == null)
            return null;

        Logger.LogInformation(
            "Cleared stale local matching assignment after Redis claim disappeared: PlayerId={PlayerId}, MatchingId={MatchingId}",
            playerId,
            assignedMatchingId);

        return requestId;
    }

    private async Task HandleMatchingCancel()
    {
        if (PlayerId == null) return;

        string? requestId = ActiveMatchingRequestId;
        var errorCode = await _matchingManager.CancelMatching(PlayerId.Value);
        if (errorCode == ErrorCode.SUCCESS && requestId != null)
            _matching.ClearRequest(requestId);

        using var packet = PacketMaker.U_TO_C_MATCHING_CANCEL(errorCode);
        TrySend(packet);
    }

    bool IMatchingSessionEndpoint.TryDeliverMatchingSuccess(long matchingId, string requestId, Packet packet)
    {
        if (Connection.IsReleased || !_matching.TryAssign(matchingId, requestId))
            return false;
        if (TrySend(packet))
            return true;

        _matching.Clear(matchingId);
        return false;
    }

    bool IMatchingSessionEndpoint.TryDeliverMatchingFailed(long matchingId, string requestId, Packet packet)
    {
        if (!_matching.FailRequest(requestId, matchingId))
            return false;
        return TrySend(packet);
    }

    bool IMatchingSessionEndpoint.TryDeliverAdmissionFailed(long matchingId, Packet packet)
    {
        if (!_matching.FailAdmission(matchingId))
            return true;
        return TrySend(packet);
    }

    void IMatchingSessionEndpoint.ClearMatchingAssignment(long matchingId) => _matching.Clear(matchingId);

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


    private void Disconnect()
    {
        Connection.Disconnect();
    }

    protected override void SendErrorResponse(ErrorCode errorCode, string message)
    {
        try
        {
            using var packet = PacketMaker.U_TO_C_ERROR(errorCode, message);
            TrySend(packet);
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
            Connection.TrySendAndDisconnect(packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send final error response: PlayerId={PlayerId}", PlayerId);
            Connection.Disconnect();
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
        var lastSuccessfulRenewal = DateTimeOffset.UtcNow;

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
        var renewalCts = Interlocked.Exchange(ref _ownershipRenewalCts, null);
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

        var lease = Interlocked.Exchange(ref _sessionLease, null);
        if (lease != null)
        {
            bool released = await _sessionOwnership.TryReleaseAsync(lease);
            Logger.LogInformation(
                "Session ownership release completed: PlayerId={PlayerId}, Generation={Generation}, Released={Released}",
                lease.PlayerId, lease.Generation, released);
        }

        if (!cleanupMatching)
            return;

        long matchingId = _matching.Take();
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
