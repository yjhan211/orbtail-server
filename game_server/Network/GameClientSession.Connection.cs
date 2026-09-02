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

        bool registered = false;
        try
        {
            GameHandoffContext? handoff = await _consumeGameHandoffTicket(msg.GameHandoffTicket);
            if (handoff == null)
            {
                EnsureConnectionActive();
                Logger.LogWarning(
                    "GameServer connection rejected: invalid, expired, replayed, or another node's handoff ticket");
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
            EnsureConnectionActive();

            Logger.LogInformation(
                "Client connection authorized: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);

            // 세션 등록은 매치 잠금 안에서 — 사람 없음 정리가 등록과 발행 사이로 끼어들 수 없다.
            MatchRuntime runtime = _matchRuntimes.GetOrCreate(matchingId);
            Action? disconnectSupersededSession = null;
            using (_matchRuntimes.Enter(runtime))
            {
                if (runtime.IsTerminal)
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

                if (!Token.TryRunIfActive(
                        () => disconnectSupersededSession = _registerSessionCallback(playerId, this)))
                    throw new OperationCanceledException("Connection closed before session registration.");
                registered = true;
            }
            disconnectSupersededSession?.Invoke();

            Logger.LogInformation(
                "Target chain restored: PlayerId={PlayerId}, Target={Target}",
                PlayerId,
                TargetPlayerId);

            RunUnderLiveMatch(runtime, () =>
            {
                foreach (var rosterEntry in handoff.HumanRoster)
                {
                    _matchRosterManager.RegisterEntry(matchingId, new RosterEntry
                    {
                        PlayerId = rosterEntry.PlayerId,
                        TargetPlayerId = rosterEntry.TargetPlayerId
                    });
                }
            });

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

            int connectedBotCount = 0;
            RunUnderLiveMatch(runtime, () =>
            {
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

                connectedBotCount = _botPlayerManager.GetBots(matchingId).Count;
                MatchStartGate.RegisterHumanPlayer(matchingId, PlayerId.Value, connectedBotCount);
            });

            // 초기 위치는 소비한 서버 발급 handoff에서만 복원한다. PlayerInfo는 존재 확인만 한다 —
            // Game Server는 PlayerInfo.Save를 부르지 않으므로(호출처는 user_server뿐) Last*를 여기서
            // 바꿔도 Redis에 남지 않고, 분산 락도 지킬 쓰기가 없어 잡지 않는다 (#335).
            {
                var playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);
                if (playerInfo == null)
                    throw new InvalidOperationException($"PlayerInfo not found for authenticated player {playerId}.");

                var matchingSpawnCell = Cell.Clone(handoff.SpawnPosition);
                _lastValidatedPosition = CellToWorldPosition(matchingSpawnCell);
                _lastValidCell = Cell.Clone(matchingSpawnCell);
                _lastValidatedRotation = 0f;
                CurrentArea = GameMapData.GetCurrentArea(CurrentMapId, matchingSpawnCell);
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
            RunUnderLiveMatch(
                runtime,
                () => _doorStateManager.InitializeMatching(CurrentMapSubId, Array.Empty<AreaType>()));
            SendDoorStateList();

            // 스웜 모드(M4)는 시간 웨이브 폐쇄를 쓰므로 폐쇄 스냅샷을 복원해야 한다.
            SendAreaClosureStateSnapshot();
            SendAreaStockStateSnapshot();

            // Send other players and broadcast this player's authoritative snapshot.
            await BroadcastPlayerJoin();
            EnsureConnectionActive();

            await _admissionStateCommitter.CommitAsync(
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
            // snapshot step has completed. Build the complete success response before that
            // commit so serialization failure still follows the admission-abort path.
            SendMatchStartCountdown(matchingId);
            using Packet successResponse = CreateConnectResultPacket(
                true,
                ErrorCode.SUCCESS,
                "Connected to GameServer");
            RunUnderLiveMatch(runtime, () =>
            {
                if (!Token.TryMarkAuthenticated(() => Volatile.Write(ref _admissionCompleted, 1)))
                    throw new OperationCanceledException("Connection closed before authentication commit.");
            });
            if (!TryPublishCommittedConnectResult(successResponse))
                return;
            Logger.LogInformation("Client connected successfully: PlayerId={PlayerId}", PlayerId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to handle connect");

            // Authentication and the local admission flag commit together. Once visible, an
            // ACK publication failure is connection-local: never roll admission back or abort
            // the whole match, and never send a contradictory negative CONNECT_RESULT.
            if (Volatile.Read(ref _admissionCompleted) != 0)
            {
                CloseAfterCommittedAdmissionResponseFailure();
                return;
            }

            // 매치 잠금 안에서 이미 등록된 세션이다. 입장 중단 경로가 등록된 수신자 전원의 터미널
            // 에러/끊기를 한 번에 소유하므로 여기서 경쟁하는 CONNECT_RESULT를 보내지 않는다.
            if (registered)
            {
                if (!ReportAdmissionFailureOnce())
                {
                    MarkServerInitiatedDisconnect();
                    Token.Disconnect();
                }
                return;
            }

            // 등록 전 실패(거부된 인계 티켓 등)는 어떤 수신자 스냅샷도 이 소켓을 소유하지 않으므로
            // 직접 응답한다.
            MarkServerInitiatedDisconnect();
            SendConnectResult(false, ErrorCode.FATAL, "게임 서버 연결 처리 중 오류가 발생했습니다",
                disconnectAfterSend: true);
        }
    }

    /// <summary>
    ///     입장 초기화 블록을 매치 잠금 안에서 돌린다 — await 사이에 매치가 끝났으면 상태를 되살리는 대신
    ///     입장을 중단한다.
    /// </summary>
    private void RunUnderLiveMatch(MatchRuntime runtime, Action initialize)
    {
        using MatchScope scope = _matchRuntimes.Enter(runtime);
        if (runtime.IsTerminal)
            throw new OperationCanceledException("Match became terminal during game admission.");

        initialize();
    }

    private void EnsureConnectionActive()
    {
        if (Token.IsReleased)
            throw new OperationCanceledException("Connection closed during game admission.");
    }

    private bool SendConnectResult(bool success, ErrorCode errorCode, string message,
        bool disconnectAfterSend = false)
    {
        using Packet packet = CreateConnectResultPacket(success, errorCode, message);
        return disconnectAfterSend ? Token.TrySendAndDisconnect(packet) : Token.TrySend(packet);
    }

    /// <summary>
    ///     Creates a complete CONNECT_RESULT frame. Successful admission calls this before the authentication commit,
    ///     keeping allocation, serialization, and body construction on the pre-commit failure side of the boundary.
    /// </summary>
    private Packet CreateConnectResultPacket(bool success, ErrorCode errorCode, string message)
    {
        var response = new G_TO_C_CONNECT_RESULT
        {
            Success = success,
            ErrorCode = errorCode,
            Message = message
        };
        byte[] body = MessagePackSerializer.Serialize(response);
        Packet packet = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT, PlayerId ?? 0);
        try
        {
            packet.SetBody(body);
            return packet;
        }
        catch
        {
            packet.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     인증 커밋 뒤 매치 잠금 밖에서 미리 만든 성공 응답을 큐에 넣는다. false나 예외는 fail-forward —
    ///     커밋된 입장은 유지하고 이 소켓만 닫는다.
    /// </summary>
    private bool TryPublishCommittedConnectResult(Packet packet)
    {
        try
        {
            if (_trySendConnectSuccessResponse(packet))
                return true;

            Logger.LogWarning(
                "Committed game admission response was not queued; closing connection: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                CurrentMapSubId);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Committed game admission response enqueue failed; closing connection: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                CurrentMapSubId);
        }

        CloseAfterCommittedAdmissionResponseFailure();
        return false;
    }

    /// <summary>
    ///     Closes a connection whose admission already committed without reporting an admission failure or attempting
    ///     a contradictory negative CONNECT_RESULT.
    /// </summary>
    private void CloseAfterCommittedAdmissionResponseFailure()
    {
        MarkServerInitiatedDisconnect();
        try
        {
            Token.Disconnect();
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to close connection after committed admission response failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                CurrentMapSubId);
        }
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

            // 내 스냅샷 로드 — 읽기 전용이라 분산 락은 잡지 않는다 (#335).
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

    /// <summary>
    ///     로드한 PlayerInfo에 세션의 현재 위치·상태를 덮어 패킷 스냅샷을 만든다. Last*·ObjectInfo는
    ///     동봉 패킷(G_TO_C_PLAYER_INFO·AREA_PLAYER_ENTER)용이며 Redis에는 저장하지 않는다 (#335).
    /// </summary>
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
