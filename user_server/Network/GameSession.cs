using Microsoft.Extensions.Logging;
using network.application.authentication;
using network.common;
using network.common.data;
using network.common.data.models;
using network.contracts.authentication;
using network.core;
using network.interfaces;
using network.packets;
using network.routing;
using user_server.services;
using user_server.services.scaling;

namespace user_server.network;

public sealed class GameSession : SessionBase
{
    private const int MaxMatchingDeliveryReceipts = 256;
    private readonly IMatchingManager _matchingManager;
    private readonly IAccountTokenService _accountTokenService;
    private readonly IUserServerCoordinationStore _coordinationStore;
    private readonly UserServerClusterOptions _clusterOptions;
    private readonly UserServerProcessIdentity _processIdentity;
    private readonly IMatchingDeliveryRouter _deliveryRouter;
    private readonly Func<long, GameSession, Action?> _onSessionRegistered;
    private readonly Func<long, GameSession, bool> _onSessionRemoved;
    private readonly IPlayerService _playerService;
    private readonly IProtocolRouter _subscribeRouter;
    private readonly MatchingDeliveryReceiptCache _matchingDeliveryReceipts =
        new(MaxMatchingDeliveryReceipts);
    private readonly object _matchingAssignmentLock = new();
    private UserSessionOwner? _sessionOwner;
    private string? _activeMatchingRequestId;
    private long _assignedMatchingId;
    private int _authenticationCommitted;

    public GameSession(
        UserToken token,
        ILogger logger,
        ICacheHelper cacheHelper,
        IRedLockFactory redLock,
        IPlayerService playerService,
        IMatchingManager matchingManager,
        IAccountTokenService accountTokenService,
        IUserServerCoordinationStore coordinationStore,
        UserServerClusterOptions clusterOptions,
        UserServerProcessIdentity processIdentity,
        IMatchingDeliveryRouter deliveryRouter,
        Func<long, GameSession, Action?> onSessionRegistered,
        Func<long, GameSession, bool> onSessionRemoved)
        : base(token, logger, cacheHelper, redLock)
    {
        _playerService = playerService;
        _matchingManager = matchingManager;
        _accountTokenService = accountTokenService;
        _coordinationStore = coordinationStore;
        _clusterOptions = clusterOptions;
        _processIdentity = processIdentity;
        _deliveryRouter = deliveryRouter;
        _onSessionRegistered = onSessionRegistered;
        _onSessionRemoved = onSessionRemoved;

        _subscribeRouter = new ProtocolRouter();
        // ReSharper disable once VirtualMemberCallInConstructor
        InitializeProtocolHandlers();
    }

    public new long? PlayerId { get; private set; }
    public PlayerInfo? PlayerInfo { get; private set; }
    internal string SessionId { get; } = Guid.NewGuid().ToString("N");
    internal UserSessionOwner? SessionOwner => Volatile.Read(ref _sessionOwner);
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
            !UserServerClusterOptions.IsSafeTokenComponent(requestId))
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

    private string? TryBeginMatchingRequest()
    {
        lock (_matchingAssignmentLock)
        {
            if (!IsConnected ||
                _assignedMatchingId != 0 ||
                _activeMatchingRequestId != null)
            {
                return null;
            }

            _activeMatchingRequestId = Guid.NewGuid().ToString("N");
            return _activeMatchingRequestId;
        }
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

    internal MatchingDeliveryResponse HandleMatchingDelivery(MatchingDeliveryRequest request)
    {
        return HandleMatchingDeliveryCore(request, requireDistributedOwner: true);
    }

    internal MatchingDeliveryResponse HandleLocalMatchingDelivery(MatchingDeliveryRequest request)
    {
        return HandleMatchingDeliveryCore(request, requireDistributedOwner: false);
    }

    private MatchingDeliveryResponse HandleMatchingDeliveryCore(
        MatchingDeliveryRequest request,
        bool requireDistributedOwner)
    {
        ArgumentNullException.ThrowIfNull(request);

        MatchingDeliveryStatus status = _matchingDeliveryReceipts.ApplyOnce(
            request,
            () => ApplyMatchingDelivery(request, requireDistributedOwner));
        return MatchingDeliveryResponse.Create(request.DeliveryId, status);
    }

    private MatchingDeliveryStatus ApplyMatchingDelivery(
        MatchingDeliveryRequest request,
        bool requireDistributedOwner)
    {
        UserSessionOwner? owner = SessionOwner;
        if (PlayerId != request.PlayerId ||
            (requireDistributedOwner &&
             (owner == null ||
              !string.Equals(owner.NodeId, request.OwnerNodeId, StringComparison.Ordinal) ||
              !string.Equals(owner.NodeGeneration, request.OwnerNodeGeneration, StringComparison.Ordinal) ||
              !string.Equals(owner.SessionId, request.OwnerSessionId, StringComparison.Ordinal) ||
              owner.SessionGeneration != request.OwnerSessionGeneration)))
        {
            return MatchingDeliveryStatus.StaleOwner;
        }

        switch (request.Kind)
        {
            case MatchingDeliveryKind.MatchingSucceeded:
                if (request.ProtocolId != (int)Protocol.U_TO_C_MATCHING_SUCCESS ||
                    request.Payload.Length == 0)
                {
                    return MatchingDeliveryStatus.InvalidRequest;
                }
                try
                {
                    using var successPacket = Packet.CreateForSending(request.Payload);
                    if (successPacket.ProtocolId != request.ProtocolId)
                        return MatchingDeliveryStatus.InvalidRequest;
                    if (!TryAssignMatching(request.MatchingId, request.RequestId))
                        return MatchingDeliveryStatus.StaleOwner;
                    if (TrySend(successPacket))
                        return MatchingDeliveryStatus.Accepted;
                }
                catch (ArgumentException ex)
                {
                    Logger.LogWarning(ex, "Rejected malformed matching-success packet");
                    return MatchingDeliveryStatus.InvalidRequest;
                }
                ClearMatchingAssignment(request.MatchingId);
                return MatchingDeliveryStatus.SessionUnavailable;

            case MatchingDeliveryKind.MatchingFailed:
                if (request.ProtocolId != (int)Protocol.U_TO_C_MATCHING_FAILED ||
                    request.Payload.Length == 0)
                {
                    return MatchingDeliveryStatus.InvalidRequest;
                }
                try
                {
                    using var failedPacket = Packet.CreateForSending(request.Payload);
                    if (failedPacket.ProtocolId != request.ProtocolId)
                        return MatchingDeliveryStatus.InvalidRequest;
                    if (!TryFailMatchingRequest(request.RequestId, request.MatchingId))
                        return MatchingDeliveryStatus.StaleOwner;
                    return TrySend(failedPacket)
                        ? MatchingDeliveryStatus.Accepted
                        : MatchingDeliveryStatus.SessionUnavailable;
                }
                catch (ArgumentException ex)
                {
                    Logger.LogWarning(ex, "Rejected malformed matching-failed packet");
                    return MatchingDeliveryStatus.InvalidRequest;
                }

            case MatchingDeliveryKind.MatchingAdmissionFailed:
                if (request.ProtocolId != (int)Protocol.U_TO_C_MATCHING_FAILED ||
                    request.Payload.Length == 0)
                {
                    return MatchingDeliveryStatus.InvalidRequest;
                }
                try
                {
                    using var admissionFailedPacket = Packet.CreateForSending(request.Payload);
                    if (admissionFailedPacket.ProtocolId != request.ProtocolId)
                        return MatchingDeliveryStatus.InvalidRequest;
                    if (!TryFailMatchingAdmission(request.MatchingId))
                        return MatchingDeliveryStatus.Accepted;
                    return TrySend(admissionFailedPacket)
                        ? MatchingDeliveryStatus.Accepted
                        : MatchingDeliveryStatus.SessionUnavailable;
                }
                catch (ArgumentException ex)
                {
                    Logger.LogWarning(ex, "Rejected malformed matching-admission-failed packet");
                    return MatchingDeliveryStatus.InvalidRequest;
                }

            case MatchingDeliveryKind.ClearMatchingAssignment:
                if (request.ProtocolId != 0 || request.Payload.Length != 0)
                    return MatchingDeliveryStatus.InvalidRequest;
                ClearMatchingAssignment(request.MatchingId);
                return MatchingDeliveryStatus.Accepted;

            case MatchingDeliveryKind.DisconnectSupersededSession:
                if (request.ProtocolId != 0 || request.Payload.Length != 0)
                    return MatchingDeliveryStatus.InvalidRequest;
                DisconnectForDuplicateLogin();
                return MatchingDeliveryStatus.Accepted;

            default:
                return MatchingDeliveryStatus.InvalidRequest;
        }
    }

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

        // 구독 프로토콜
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
        if (PlayerId.HasValue || PlayerInfo != null)
        {
            Logger.LogWarning("Repeated login attempt on an authenticated session: PlayerId={PlayerId}", PlayerId);
            SendErrorResponseAndDisconnect(ErrorCode.AUTH_FAILED, "이미 인증된 세션입니다");
            return;
        }

        bool authenticationCommitted = false;
        UserSessionOwnerAcquisition? ownerAcquisition = null;
        try
        {
            Logger.LogInformation("Login request received: HasAccountCredential={HasAccountCredential}",
                !string.IsNullOrWhiteSpace(msg.AccountToken));

            AccountTokenResolution account = await _accountTokenService.ResolveAsync(msg.AccountToken);
            long playerId = account.PlayerId;

            PlayerId = playerId;
            Logger.LogInformation("PlayerId set to {PlayerId}", PlayerId);

            await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
            Logger.LogInformation("Player lock acquired for PlayerId={PlayerId}", PlayerId);

            PlayerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

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

            if (_clusterOptions.Enabled)
            {
                ownerAcquisition = await _coordinationStore.TryAcquireOrReplaceSessionOwnerAsync(
                    _processIdentity,
                    PlayerId.Value,
                    SessionId,
                    _clusterOptions.SessionOwnerLifetime);
                if (ownerAcquisition == null)
                {
                    throw new InvalidOperationException(
                        "Could not acquire the distributed session owner because this UserServer node lease is not active.");
                }

                Volatile.Write(ref _sessionOwner, ownerAcquisition.CurrentOwner);
            }

            // 로그인 응답 전송
            Logger.LogInformation("Creating login packet for PlayerId={PlayerId}", PlayerId);
            using var loginPacket = PacketMaker.U_TO_C_LOGIN(PlayerInfo, account.AccountToken);
            Logger.LogInformation("Sending U_TO_C_LOGIN packet for PlayerId={PlayerId}, Packet size={Size}", PlayerId,
                loginPacket.ToBytes().Length);
            Action? disconnectSupersededSession = null;
            if (!Token.TryMarkAuthenticated(
                    () =>
                    {
                        disconnectSupersededSession = _onSessionRegistered(PlayerId.Value, this);
                        Volatile.Write(ref _authenticationCommitted, 1);
                    }))
                throw new OperationCanceledException("Connection closed before login admission.");
            authenticationCommitted = true;
            disconnectSupersededSession?.Invoke();
            if (ownerAcquisition?.PreviousOwner is { } previousOwner &&
                (!string.Equals(previousOwner.NodeId, _processIdentity.NodeId, StringComparison.Ordinal) ||
                 !string.Equals(
                     previousOwner.NodeGeneration,
                     _processIdentity.Generation,
                     StringComparison.Ordinal)))
            {
                QueueRemoteDuplicateDisconnect(previousOwner);
            }
            Logger.LogInformation("Session registered for PlayerId={PlayerId}", PlayerId);
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
        catch (AccountAuthenticationException ex)
        {
            if (!authenticationCommitted)
                await RestoreUncommittedSessionOwnerAsync(ownerAcquisition);
            Logger.LogWarning(ex, "Login authentication failed");
            SendErrorResponseAndDisconnect(ErrorCode.AUTH_FAILED, "계정 인증에 실패했습니다");
        }
        catch (Exception ex)
        {
            if (!authenticationCommitted)
                await RestoreUncommittedSessionOwnerAsync(ownerAcquisition);
            Logger.LogError(ex, "Login failed");
            SendErrorResponseAndDisconnect(ErrorCode.SERVER_INTERNAL_ERROR, "로그인 처리 중 오류가 발생했습니다");
        }
    }

    private async Task RestoreUncommittedSessionOwnerAsync(
        UserSessionOwnerAcquisition? acquisition)
    {
        UserSessionOwner? owner = Interlocked.Exchange(ref _sessionOwner, null);
        if (owner == null)
            return;

        try
        {
            if (acquisition == null ||
                acquisition.CurrentOwner != owner ||
                !await _coordinationStore.RestorePreviousSessionOwnerAsync(
                    acquisition,
                    _clusterOptions.SessionOwnerLifetime))
            {
                Logger.LogWarning(
                    "Uncommitted distributed session owner could not restore its previous owner: PlayerId={PlayerId}, SessionId={SessionId}",
                    owner.PlayerId,
                    owner.SessionId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to restore the previous distributed session owner: PlayerId={PlayerId}, SessionId={SessionId}",
                owner.PlayerId,
                owner.SessionId);
        }
    }

    private void QueueRemoteDuplicateDisconnect(UserSessionOwner previousOwner)
    {
        string deliveryId = Guid.NewGuid().ToString("N");
        var request = new MatchingDeliveryRequest
        {
            DeliveryId = deliveryId,
            Kind = MatchingDeliveryKind.DisconnectSupersededSession,
            PlayerId = previousOwner.PlayerId,
            MatchingId = 0,
            RequestId = SessionId,
            OwnerNodeId = previousOwner.NodeId,
            OwnerNodeGeneration = previousOwner.NodeGeneration,
            OwnerSessionId = previousOwner.SessionId,
            OwnerSessionGeneration = previousOwner.SessionGeneration
        };

        if (!_matchingManager.TryRunBackgroundOperation(
                async () =>
                {
                    MatchingDeliveryResponse response = await _deliveryRouter.DeliverAsync(request);
                    if (response.Status is not (
                            MatchingDeliveryStatus.Accepted or
                            MatchingDeliveryStatus.StaleOwner or
                            MatchingDeliveryStatus.SessionUnavailable))
                    {
                        Logger.LogWarning(
                            "Remote duplicate-session disconnect was not accepted: PlayerId={PlayerId}, Status={Status}",
                            previousOwner.PlayerId,
                            response.Status);
                    }
                },
                $"disconnect superseded remote session {previousOwner.PlayerId}/{previousOwner.SessionId}"))
        {
            Logger.LogWarning(
                "Remote duplicate-session disconnect was rejected during shutdown: PlayerId={PlayerId}",
                previousOwner.PlayerId);
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
                itemUidUpperBound = await CacheHelper.StringIncrementByAsync(
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
            await playerInfo.Save(CacheHelper);

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

    // ========== 채팅 ==========

    // ========== 매칭 ==========

    private async Task HandleMatching(C_TO_U_MATCHING msg)
    {
        if (PlayerId == null) return;

        string? requestId = TryBeginMatchingRequest();
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

    public override void OnRemoved()
    {
        Logger.LogInformation("Session removed: PlayerId={PlayerId}", PlayerId);

        if (!PlayerId.HasValue)
            return;

        long playerId = PlayerId.Value;
        bool removedLocally = _onSessionRemoved(playerId, this);
        UserSessionOwner? owner = SessionOwner;
        if (_clusterOptions.Enabled && owner != null)
        {
            if (Volatile.Read(ref _authenticationCommitted) == 0)
                return;

            if (!_matchingManager.TryRunBackgroundOperation(
                    async () =>
                    {
                        bool released = await _coordinationStore.ReleaseSessionOwnerAsync(owner);
                        if (released)
                            Interlocked.CompareExchange(ref _sessionOwner, null, owner);
                        if (removedLocally && released)
                            await CleanupRemovedSessionMatchingAsync(playerId);
                    },
                    $"release distributed session owner {playerId}/{owner.SessionId}"))
            {
                Logger.LogWarning(
                    "Distributed session-owner release was rejected during shutdown: PlayerId={PlayerId}, SessionId={SessionId}",
                    playerId,
                    owner.SessionId);
            }
            return;
        }

        if (removedLocally)
            _matchingManager.TryRunBackgroundOperation(
                () => CleanupRemovedSessionMatchingAsync(playerId),
                $"cleanup disconnected player {playerId}");
    }

    private async Task CleanupRemovedSessionMatchingAsync(long playerId)
    {
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
