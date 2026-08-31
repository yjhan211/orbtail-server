using System.Collections.Generic;
using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.contracts.authentication;
using network.helpers;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private async Task HandleConnect(C_TO_G_CONNECT msg)
    {
        if (PlayerId.HasValue || CurrentMapSubId > 0)
        {
            Logger.LogWarning(
                "Repeated game authentication attempt: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                CurrentMapSubId);
            SendConnectResult(false, ErrorCode.AUTH_FAILED, "이미 인증된 세션입니다", disconnectAfterSend: true);
            return;
        }

        IDisposable? runtimeOperation = null;
        try
        {
            GameHandoffContext? handoff = await _consumeGameHandoffTicket(msg.GameHandoffTicket);
            if (handoff == null)
            {
                EnsureConnectionActive();
                Logger.LogWarning("GameServer connection rejected: invalid, expired, or replayed handoff ticket");
                SendConnectResult(false, ErrorCode.AUTH_FAILED, "게임 접속 인증에 실패했습니다",
                    disconnectAfterSend: true);
                return;
            }

            long matchingId = handoff.MatchingId;
            long playerId = handoff.PlayerId;
            // Commit the ticket identity immediately after a successful consume response and
            // before checking the socket state. If a legacy GETDEL response itself is lost,
            // no exact identity exists here; the UserServer admission deadline owns rollback.
            PlayerId = playerId;
            CurrentMapId = handoff.MapId;
            CurrentMapSubId = handoff.MapSubId;
            TargetPlayerId = handoff.TargetPlayerId;
            SetActiveBuffIds(handoff.ActiveBuffIds);
            Volatile.Write(
                ref _handoffHumanPlayerIds,
                handoff.HumanRoster.Select(entry => entry.PlayerId)
                    .Append(playerId)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToArray());
            if (!_bindMatchOwnerFence(matchingId, handoff.GameServerFence))
                throw new InvalidOperationException(
                    $"The handoff owner fence does not match the active runtime for match {matchingId}.");
            EnsureConnectionActive();

            Logger.LogInformation(
                "Client connection authorized: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);

            Action? disconnectSupersededSession = null;
            runtimeOperation = _acquireMatchRuntimeOperation(
                matchingId,
                () =>
                {
                    if (!Token.TryRunIfActive(
                            () => disconnectSupersededSession = _registerSessionCallback(playerId, this)))
                        throw new OperationCanceledException("Connection closed before session registration.");
                });
            if (runtimeOperation == null)
            {
                Logger.LogWarning(
                    "GameServer connection rejected because the match is terminal: PlayerId={PlayerId}, MatchingId={MatchingId}",
                    playerId,
                    matchingId);
                MarkServerInitiatedDisconnect();
                SendConnectResult(false, ErrorCode.GAME_ALREADY_ENDED, "이미 종료된 게임입니다",
                    disconnectAfterSend: true);
                return;
            }
            disconnectSupersededSession?.Invoke();

            Logger.LogInformation(
                "Target chain restored: PlayerId={PlayerId}, Target={Target}",
                PlayerId,
                TargetPlayerId);

            foreach (var rosterEntry in handoff.HumanRoster)
            {
                _matchRosterManager.RegisterEntry(matchingId, new RosterEntry
                {
                    PlayerId = rosterEntry.PlayerId,
                    TargetPlayerId = rosterEntry.TargetPlayerId
                });
            }

            // Load bots once per match before initializing authoritative roster state.
            int expectedBotCount = MatchStartGate.IsSoloMapValidationEnabled
                ? 0
                : Config.SWARM_PLAYERS_PER_MATCH - handoff.HumanRoster.Count;
            if (expectedBotCount < 0)
                throw new InvalidOperationException(
                    $"Game handoff contains too many human players: {handoff.HumanRoster.Count}.");
            await LoadBotsIfNeeded(
                matchingId,
                CurrentMapId,
                expectedBotCount,
                handoff.HumanRoster.Select(entry => entry.PlayerId).ToArray());
            EnsureConnectionActive();

            foreach (var bot in _botPlayerManager.GetBots(matchingId))
                if (!bot.IsEliminated)
                {
                    _gameEventLogManager.SetPlayerArea(matchingId, bot.PlayerId, bot.CurrentArea.ToString());
                }

            // Initialize match-scoped area state once; manager implementations are idempotent.
            _areaItemStockManager.InitializeMatching(matchingId);
            _groundItemManager.InitializeMatching(matchingId);
            int matchSeed = MatchSpawnData.GetDeterministicSeed(matchingId);
            _gameEventLogManager.BeginMatch(matchingId, matchSeed);
            foreach (var bot in _botPlayerManager.GetBots(matchingId))
            {
                _gameEventLogManager.LogSpawnAssignment(
                    matchingId,
                    bot.PlayerId,
                    matchSeed,
                    MatchSpawnData.GetAnchorIndex(bot.Cell),
                    bot.Cell.X,
                    bot.Cell.Y,
                    bot.CurrentArea.ToString(),
                    isBot: true);
            }

            // The authenticated session was registered while acquiring the runtime lease.
            int connectedBotCount = _botPlayerManager.GetBots(matchingId).Count;
            MatchStartGate.RegisterHumanPlayer(matchingId, PlayerId.Value, connectedBotCount);

            // Restore the initial position only from the consumed server-issued handoff.
            {
                await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
                var playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);
                if (playerInfo == null)
                    throw new InvalidOperationException($"PlayerInfo not found for authenticated player {playerId}.");

                var matchingSpawnCell = Cell.Clone(handoff.SpawnPosition);
                playerInfo.LastMapId = CurrentMapId;
                playerInfo.LastMapSubId = CurrentMapSubId;
                playerInfo.LastCell = Cell.Clone(matchingSpawnCell);
                playerInfo.ObjectInfo.MapId = CurrentMapId;
                playerInfo.ObjectInfo.MapSubId = CurrentMapSubId;
                playerInfo.ObjectInfo.Cell = Cell.Clone(matchingSpawnCell);
                playerInfo.ObjectInfo.Position = CellToWorldPosition(matchingSpawnCell);

                _lastValidatedPosition = playerInfo.ObjectInfo.Position;
                _lastValidCell = playerInfo.ObjectInfo.Cell;
                _lastValidatedRotation = playerInfo.ObjectInfo.Rotation;
                CurrentArea = GameMapData.GetCurrentArea(CurrentMapId, playerInfo.ObjectInfo.Cell);
                Logger.LogInformation(
                    "Player {PlayerId} initial Area: {Area}, Position: ({PosX:F2},{PosY:F2}), Cell: ({CellX},{CellY})",
                    PlayerId, CurrentArea, _lastValidatedPosition?.X, _lastValidatedPosition?.Y, _lastValidCell?.X,
                    _lastValidCell?.Y);
                _gameEventLogManager.LogSpawnAssignment(
                    CurrentMapSubId,
                    PlayerId.Value,
                    MatchSpawnData.GetDeterministicSeed(CurrentMapSubId),
                    MatchSpawnData.GetAnchorIndex(matchingSpawnCell),
                    matchingSpawnCell.X,
                    matchingSpawnCell.Y,
                    CurrentArea.ToString(),
                    isBot: false);
                _gameEventLogManager.SetPlayerArea(CurrentMapSubId, PlayerId.Value, CurrentArea.ToString());

                if (CurrentArea != AreaType.None)
                {
                    SendInteractableList(CurrentArea);
                    SendInteractCooldownSnapshot();
                    SendGroundItemSnapshot(CurrentArea);
                }
            }
            EnsureConnectionActive();

            SendInGameInventoryList();
            SendSummonStoneState();
            var connectionBoard = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
            _gameEventLogManager.LogOrbBoardTransition(
                CurrentMapSubId, PlayerId.Value, connectionBoard.GetAllItems(),
                connectionBoard.GetEquippedBattleItem()?.ItemId ?? 0, CurrentArea.ToString(), "connection_sync", isBot: false);

            // Send the initial door and mission snapshots.
            _doorStateManager.InitializeMatching(CurrentMapSubId, Array.Empty<AreaType>());
            SendDoorStateList();

            // 스웜 모드(M4)는 시간 웨이브 폐쇄를 쓰므로 폐쇄 스냅샷을 복원해야 한다.
            SendAreaClosureStateSnapshot();
            SendAreaStockStateSnapshot();

            // Send other players and broadcast this player's authoritative snapshot.
            await BroadcastPlayerJoin();
            EnsureConnectionActive();

            await CommitAdmissionAsync(
                matchingId,
                PlayerId.Value,
                handoff.HumanRoster.Select(entry => entry.PlayerId)
                    .Append(PlayerId.Value)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToArray());
            EnsureConnectionActive();

            // The standalone submission client is only ready after the full initial snapshot
            // has been sent. Starting the countdown earlier lets bots consume finite room stock
            // while the human client is still loading the match.
            int singleHumanBotCount = Config.SWARM_PLAYERS_PER_MATCH - 1;
            if (connectedBotCount == singleHumanBotCount || MatchStartGate.IsSoloMapValidationEnabled)
            {
                MatchStartGate.MarkHumanReady(matchingId, PlayerId.Value);
            }

            // Authentication succeeds only after every fallible initialization and initial
            // snapshot step has completed. This prevents success -> failure double responses.
            SendMatchStartCountdown(matchingId);
            bool admissionCommitted = _executeMatchRuntime(matchingId, () =>
            {
                if (!Token.TryMarkAuthenticated())
                    throw new OperationCanceledException("Connection closed before authentication commit.");
                if (!SendConnectResult(true, ErrorCode.SUCCESS, "Connected to GameServer"))
                    throw new OperationCanceledException("Connection closed before admission response was queued.");
                Volatile.Write(ref _admissionCompleted, 1);
            });
            if (!admissionCommitted)
                throw new OperationCanceledException("Match became terminal before authentication commit.");
            Logger.LogInformation("Client connected successfully: PlayerId={PlayerId}", PlayerId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to handle connect");

            // Move the match to Finalizing while this handler still owns its runtime lease.
            // That prevents another concurrently connecting human from committing a success ACK.
            ReportAdmissionFailureOnce();
            MarkServerInitiatedDisconnect();
            SendConnectResult(false, ErrorCode.FATAL, "게임 서버 연결 처리 중 오류가 발생했습니다",
                disconnectAfterSend: true);
        }
        finally
        {
            runtimeOperation?.Dispose();
        }
    }

    private void EnsureConnectionActive()
    {
        if (Token.IsReleased)
            throw new OperationCanceledException("Connection closed during game admission.");
    }

    private async Task CommitAdmissionAsync(
        long matchingId,
        long playerId,
        IReadOnlyCollection<long> expectedHumanPlayerIds)
    {
        string admissionStateKey = MatchingHandoffRedisKeys.AdmissionStateKey(matchingId);
        var admissionState = await CacheHelper.StringGetAsync(admissionStateKey);
        if (admissionState.IsNullOrEmpty ||
            !string.Equals(admissionState.ToString(), MatchingHandoffRedisKeys.AdmissionPendingState,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Admission is not pending for match {matchingId}: '{admissionState}'.");
        }

        string expectedClaim = matchingId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Exception? claimError = null;
        bool claimRenewed = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                claimRenewed = await CacheHelper.StringSetIfEqualsAsync(
                    MatchingHandoffRedisKeys.ClaimKey(playerId),
                    expectedClaim,
                    expectedClaim,
                    MatchingHandoffRedisKeys.Lifetime);
            }
            catch (Exception ex)
            {
                claimError = ex;
                continue;
            }

            if (!claimRenewed)
                throw new InvalidOperationException(
                    $"Matching claim changed before admission for player {playerId} in match {matchingId}.");
            break;
        }

        if (!claimRenewed)
        {
            var claim = await CacheHelper.StringGetAsync(MatchingHandoffRedisKeys.ClaimKey(playerId));
            if (claim.IsNullOrEmpty || !string.Equals(claim.ToString(), expectedClaim, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Could not renew the matching claim for player {playerId} in match {matchingId}.",
                    claimError);
            }

            Logger.LogWarning(
                claimError,
                "Matching claim renewal response was lost; exact claim read-back confirmed: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
        }

        string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
        string admittedField = MatchingHandoffRedisKeys.AdmittedPlayerField(playerId);
        await WriteAdmissionMarkerWithReadBackAsync(
            handoffKey,
            admittedField,
            MatchingHandoffRedisKeys.AdmissionReadyValue,
            $"player {playerId} admission for match {matchingId}");

        var admittedFields = expectedHumanPlayerIds
            .Select(MatchingHandoffRedisKeys.AdmittedPlayerField)
            .Select(field => (StackExchange.Redis.RedisValue)field)
            .ToArray();
        var admittedValues = await CacheHelper.HashGetAsync(handoffKey, admittedFields);
        bool allHumansAdmitted = admittedValues.Length == admittedFields.Length &&
                                 admittedValues.All(value =>
                                     !value.IsNullOrEmpty &&
                                     ((byte[])value!).AsSpan().SequenceEqual(
                                         [MatchingHandoffRedisKeys.AdmissionReadyValue]));
        if (!allHumansAdmitted)
            return;

        await CompleteAdmissionStateAsync(admissionStateKey, matchingId);
    }

    private async Task CompleteAdmissionStateAsync(string admissionStateKey, long matchingId)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool canceledStateObserved = false;
            try
            {
                bool completed = await CacheHelper.StringSetIfEqualsAsync(
                    admissionStateKey,
                    MatchingHandoffRedisKeys.AdmissionPendingState,
                    MatchingHandoffRedisKeys.AdmissionCompletedState,
                    MatchingHandoffRedisKeys.Lifetime);
                if (completed)
                    return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            try
            {
                var state = await CacheHelper.StringGetAsync(admissionStateKey);
                if (!state.IsNullOrEmpty &&
                    string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCompletedState,
                        StringComparison.Ordinal))
                {
                    if (lastError != null)
                    {
                        Logger.LogWarning(
                            lastError,
                            "Admission completion response was lost; terminal state read-back confirmed: MatchingId={MatchingId}",
                            matchingId);
                    }
                    return;
                }

                if (!state.IsNullOrEmpty &&
                    string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCanceledState,
                        StringComparison.Ordinal))
                {
                    canceledStateObserved = true;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                Logger.LogWarning(
                    ex,
                    "Admission terminal-state read-back failed: MatchingId={MatchingId}, Attempt={Attempt}",
                    matchingId,
                    attempt + 1);
            }

            if (canceledStateObserved)
            {
                throw new InvalidOperationException(
                    $"Admission timeout canceled match {matchingId} before completion.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new InvalidOperationException(
            $"Could not confirm admission completion for match {matchingId}.",
            lastError);
    }

    private async Task WriteAdmissionMarkerWithReadBackAsync(
        string key,
        string field,
        byte value,
        string description)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await CacheHelper.HashSetWithExpiryAsync(
                    key,
                    field,
                    [value],
                    MatchingHandoffRedisKeys.Lifetime);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try
                {
                    var marker = await CacheHelper.HashGetAsync(key, field);
                    if (!marker.IsNullOrEmpty && ((byte[])marker!).AsSpan().SequenceEqual([value]))
                    {
                        Logger.LogWarning(
                            ex,
                            "Redis write response was lost; read-back confirmed {Description}",
                            description);
                        return;
                    }
                }
                catch (Exception readBackError)
                {
                    Logger.LogWarning(
                        readBackError,
                        "Redis marker read-back failed for {Description}: Attempt={Attempt}",
                        description,
                        attempt + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not confirm {description}.", lastError);
    }

    private bool SendConnectResult(bool success, ErrorCode errorCode, string message,
        bool disconnectAfterSend = false)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT, PlayerId ?? 0);
        var response = new G_TO_C_CONNECT_RESULT
        {
            Success = success,
            ErrorCode = errorCode,
            Message = message
        };
        packet.SetBody(MessagePackSerializer.Serialize(response));
        return disconnectAfterSend ? Token.TrySendAndDisconnect(packet) : Token.TrySend(packet);
    }
    private async Task BroadcastPlayerJoin()
    {
        if (!PlayerId.HasValue || IsEliminated) return;

        try
        {
            var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            // Same-area players only
            var sameAreaSessions = allSessions
                .Where(s => !s.IsEliminated &&
                            s.PlayerId != PlayerId &&
                            s.CurrentArea == CurrentArea &&
                            s.PlayerId.HasValue)
                .ToList();

            // Send other players already present in the same area to this client.
            if (sameAreaSessions.Count > 0)
            {
                var playerInfoList = new List<PlayerInfo>();
                foreach (var session in sameAreaSessions)
                {
                    await using var playerLock = await PlayerInfo.Lock(RedLock, session.PlayerId!.Value);
                    var playerInfo = await PlayerInfo.Load(CacheHelper, session.PlayerId!.Value);
                    if (playerInfo == null)
                        throw new InvalidOperationException(
                            $"PlayerInfo not found for connected player {session.PlayerId.Value}.");

                    ApplyLivePlayerInfoSnapshot(session, playerInfo);
                    playerInfoList.Add(playerInfo);
                }

                if (playerInfoList.Count > 0)
                {
                    using var packet = PacketMaker.G_TO_C_PLAYER_INFO(playerInfoList);
                    Send(packet);
                    Logger.LogInformation("Sent {Count} PlayerInfo in Area {Area} to PlayerId={L}",
                        playerInfoList.Count, CurrentArea, PlayerId);
                }
            }

            // Load this player's authoritative snapshot.
            await using var myPlayerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
            var myPlayerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

            if (myPlayerInfo == null)
                throw new InvalidOperationException($"PlayerInfo not found for joining player {PlayerId.Value}.");

            ApplyLivePlayerInfoSnapshot(this, myPlayerInfo);

            // Broadcast this player's snapshot to other players in the same area.
            using var myPacket = PacketMaker.G_TO_C_PLAYER_INFO([myPlayerInfo]);
            foreach (var session in sameAreaSessions) session.Send(myPacket);
            Logger.LogInformation("Broadcasted my PlayerInfo (PlayerId={L}) to {Count} players in Area {Area}",
                PlayerId, sameAreaSessions.Count, CurrentArea);

            // Send bots already present in the same area to this client.
            var sameAreaBots = _botPlayerManager.GetBots(CurrentMapSubId)
                .Where(b => !b.IsEliminated && b.CurrentArea == CurrentArea)
                .ToList();
            if (sameAreaBots.Count > 0)
            {
                var botInfoList = new List<PlayerInfo>();
                foreach (var bot in sameAreaBots)
                {
                    var botInfo = _botPlayerManager.SynthesizePlayerInfo(CurrentMapSubId, bot.PlayerId);
                    if (botInfo != null) botInfoList.Add(botInfo);
                }
                if (botInfoList.Count > 0)
                {
                    using var botPacket = PacketMaker.G_TO_C_PLAYER_INFO(botInfoList);
                    Send(botPacket);
                    Logger.LogInformation("Sent {Count} bot PlayerInfo in Area {Area} to PlayerId={L}",
                        botInfoList.Count, CurrentArea, PlayerId);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to broadcast player join for PlayerId={L}", PlayerId);
            throw;
        }
    }

    private void ApplyLivePlayerInfoSnapshot(GameClientSession session, PlayerInfo playerInfo)
    {
        var cell = session._lastValidCell ?? playerInfo.LastCell ?? playerInfo.ObjectInfo?.Cell;
        var position = session._lastValidatedPosition;

        playerInfo.State = session.CurrentState;
        playerInfo.LastMapId = session.CurrentMapId;
        playerInfo.LastMapSubId = session.CurrentMapSubId;
        playerInfo.ObjectInfo ??= new GameObjectInfo(playerInfo.PlayerId);
        playerInfo.ObjectInfo.MapId = session.CurrentMapId;
        playerInfo.ObjectInfo.MapSubId = session.CurrentMapSubId;
        playerInfo.ObjectInfo.Rotation = session._lastValidatedRotation;

        if (cell != null)
        {
            playerInfo.LastCell = Cell.Clone(cell);
            playerInfo.ObjectInfo.Cell = Cell.Clone(cell);
            position ??= session.CellToWorldPosition(cell);
        }

        if (position != null)
            playerInfo.ObjectInfo.Position = position;

        _matchRosterManager.UpdatePlayerProfile(
            session.CurrentMapSubId,
            playerInfo.PlayerId,
            playerInfo.Name,
            playerInfo.WearItemIdList);
    }

    private void SendMatchStartCountdown(long matchingId)
    {
        var snapshot = MatchStartGate.GetSnapshot(matchingId);
        if (!snapshot.IsKnown)
            return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN, PlayerId ?? 0);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MATCH_START_COUNTDOWN
        {
            MatchingId = matchingId,
            RemainingSeconds = snapshot.RemainingSeconds,
            ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }));
        Send(packet);
    }

    private Task HandleMatchStartReady()
    {
        if (PlayerId.HasValue && CurrentMapSubId > 0)
        {
            MatchStartGate.MarkHumanReady(CurrentMapSubId, PlayerId.Value);
            SendMatchStartCountdown(CurrentMapSubId);
        }

        return Task.CompletedTask;
    }

    private Task HandleHeartbeat()
    {
        _lastHeartbeatTime = DateTime.UtcNow;

        // Send the heartbeat response.
        using var packet = PacketMaker.G_TO_C_HEART_BEAT(DateTime.UtcNow);
        Send(packet);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     하트비트 타임아웃을 확인한다. 타임아웃이면 true를 반환한다.
    /// </summary>
    public bool IsHeartbeatTimedOut()
    {
        double elapsed = (DateTime.UtcNow - _lastHeartbeatTime).TotalSeconds;
        return elapsed > HeartbeatTimeoutSeconds;
    }

    /// <summary>
    ///     Forcibly disconnects the session.
    /// </summary>
    public void ForceDisconnect()
    {
        Logger.LogWarning("Force disconnecting PlayerId={PlayerId} due to heartbeat timeout", PlayerId);
        Token.Disconnect();
    }

    /// <summary>
    ///     Redis에서 봇 정보를 로드한다. 매칭별 최초 한 번만 수행한다.
    ///     봇 위치 초기화에 필요한 MapId를 함께 전달한다.
    /// </summary>
    private async Task LoadBotsIfNeeded(
        long matchingId,
        MapId mapId,
        int expectedBotCount,
        IReadOnlyCollection<long> expectedHumanPlayerIds)
    {
        var initializationLock =
            MatchInitializationLocks.GetOrAdd(matchingId, static _ => new SemaphoreSlim(1, 1));
        await initializationLock.WaitAsync();
        try
        {
            await WaitForMatchingHandoffReadyAsync(matchingId, expectedHumanPlayerIds);

            if (_botPlayerManager.HasBots(matchingId))
            {
                int existingBotCount = _botPlayerManager.GetBots(matchingId).Count;
                if (existingBotCount != expectedBotCount)
                    throw new InvalidOperationException(
                        $"Registered bot count mismatch for match {matchingId}: expected {expectedBotCount}, found {existingBotCount}.");
                return;
            }

            string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
            var botData = await CacheHelper.HashGetDeleteFirstAsync(
                handoffKey,
                MatchingHandoffRedisKeys.BotsField,
                "matching_bots",
                matchingId);

            if (botData.IsNullOrEmpty)
            {
                if (expectedBotCount != 0)
                    throw new InvalidOperationException(
                        $"Missing bot handoff for match {matchingId}: expected {expectedBotCount} bots.");
                return;
            }

            var botInfoList = MessagePackSerializer.Deserialize<List<BotMatchingInfo>>((byte[])botData!);
            if (botInfoList == null || botInfoList.Count != expectedBotCount)
                throw new InvalidOperationException(
                    $"Bot handoff count mismatch for match {matchingId}: expected {expectedBotCount}, found {botInfoList?.Count ?? 0}.");

            var botPlayerIds = new HashSet<long>();
            foreach (BotMatchingInfo bot in botInfoList)
            {
                if (bot == null ||
                    bot.PlayerId >= 0 ||
                    bot.TargetPlayerId == 0 ||
                    bot.SpawnCell == null ||
                    (bot.SpawnCell.X == 0 && bot.SpawnCell.Y == 0) ||
                    !botPlayerIds.Add(bot.PlayerId))
                {
                    throw new InvalidOperationException(
                        $"Bot handoff contains an invalid or duplicate entry for match {matchingId}.");
                }
            }

            if (expectedBotCount == 0)
                return;

            _botPlayerManager.RegisterBots(matchingId, mapId, botInfoList);

            // 봇의 타깃 체인을 매치 로스터에 등록한다.
            foreach (var bot in botInfoList)
            {
                _matchRosterManager.RegisterEntry(matchingId, new RosterEntry
                {
                    PlayerId = bot.PlayerId,
                    TargetPlayerId = bot.TargetPlayerId
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load bot handoff: MatchingId={MatchingId}", matchingId);
            throw;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private async Task WaitForMatchingHandoffReadyAsync(
        long matchingId,
        IReadOnlyCollection<long> expectedHumanPlayerIds)
    {
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(50);
        int maxAttempts = Math.Max(
            1,
            (int)Math.Ceiling(MatchingHandoffRedisKeys.AdmissionTimeout.TotalMilliseconds /
                              retryDelay.TotalMilliseconds));
        string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
        string admissionStateKey = MatchingHandoffRedisKeys.AdmissionStateKey(matchingId);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var ready = await CacheHelper.HashGetAsync(
                handoffKey,
                MatchingHandoffRedisKeys.AdmissionReadyField);
            if (!ready.IsNullOrEmpty)
            {
                byte[] value = (byte[])ready!;
                if (value.Length == 1 && value[0] == MatchingHandoffRedisKeys.AdmissionReadyValue)
                {
                    string expectedClaim = matchingId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    foreach (long humanPlayerId in expectedHumanPlayerIds)
                    {
                        var claim = await CacheHelper.StringGetAsync(
                            MatchingHandoffRedisKeys.ClaimKey(humanPlayerId));
                        if (claim.IsNullOrEmpty || !string.Equals(claim.ToString(), expectedClaim,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Matching claim is not active for player {humanPlayerId} in match {matchingId}.");
                        }
                    }
                    return;
                }
                throw new InvalidOperationException(
                    $"Invalid admission marker for match {matchingId}.");
            }

            var admissionState = await CacheHelper.StringGetAsync(admissionStateKey);
            if (!admissionState.IsNullOrEmpty &&
                string.Equals(
                    admissionState.ToString(),
                    MatchingHandoffRedisKeys.AdmissionCanceledState,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Matching admission was canceled before the handoff became ready for match {matchingId}.");
            }

            if (attempt + 1 < maxAttempts)
                await Task.Delay(retryDelay);
        }

        throw new TimeoutException(
            $"Matching handoff was not committed for match {matchingId}.");
    }

    /// <summary>
    /// 새 접속과 재접속 시 현재 방송 대상·남은 시간·누적 폐쇄 지역을 복원한다.
    /// 미래 웨이브는 아직 보내지 않아 다음 방송 전까지 대상이 드러나지 않는다.
    /// </summary>
    private void SendAreaClosureStateSnapshot()
    {
        if (CurrentMapSubId <= 0) return;

        var snapshot = _areaClosureManager.GetClientStateSnapshot(CurrentMapSubId);
        foreach (var closedArea in snapshot.ClosedAreas)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSED
            {
                AreaType = closedArea,
                SuppressAlert = true
            }));
            Send(packet);
        }

        foreach (var warningArea in snapshot.WarningAreas)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
            {
                AreaType = warningArea,
                SecondsRemaining = snapshot.WarningSeconds,
                ClosureAtUnixMs = snapshot.ClosureAtUnixMs
            }));
            Send(packet);
        }
        var globalClosure = _areaClosureManager.GetGlobalClosureClientState(CurrentMapSubId);
        if (globalClosure.IsKnown)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
            {
                AreaType = AreaType.None,
                SecondsRemaining = globalClosure.SecondsRemaining,
                ClosureAtUnixMs = globalClosure.ClosureAtUnixMs,
                IsGlobalClosure = true,
                IsGlobalClosureActive = globalClosure.IsActive
            }));
            Send(packet);
        }

        // AreaType.None is the generic next-warning clock.
        using var countdownPacket = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
        countdownPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
        {
            AreaType = AreaType.None,
            SecondsRemaining = snapshot.NextWarningSeconds,
            ClosureAtUnixMs = snapshot.NextWarningAtUnixMs
        }));
        Send(countdownPacket);

        // #272 자기장: 수축 시계를 복원한다 — 클라 경계 렌더의 유일한 입력. 폐쇄 시계와
        // 같은 앵커(GameStartTime)라 별도 상태가 없다.
        var closureState = _areaClosureManager.GetMatchingState(CurrentMapSubId);
        if (Config.SWARM_PRESSURE_FIELD_ENABLED && closureState != null)
        {
            using var fieldPacket = Packet.Create((int)Protocol.G_TO_C_SWARM_FIELD_STATE);
            fieldPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_FIELD_STATE
            {
                StartedAtUnixMs = new DateTimeOffset(closureState.GameStartTime).ToUnixTimeMilliseconds()
            }));
            Send(fieldPacket);
        }
    }

}
