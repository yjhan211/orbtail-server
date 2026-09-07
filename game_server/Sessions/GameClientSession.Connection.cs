using System.Collections.Generic;
using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.gamehandoff;
using network.helpers;
using network.infrastructure.redis;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    private async Task HandleConnect(C_TO_G_CONNECT msg)
    {
        if (PlayerId.HasValue || MatchingId > 0)
        {
            Logger.LogWarning(
                "Repeated game authentication attempt: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                MatchingId);
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
            // 신원은 소비 응답 직후, 소켓 상태 확인 전에 확정한다. GETDEL 응답이 유실되면 여기에 정확한 신원이
            // 없고, 되돌리기는 user_server의 입장 마감이 맡는다.
            PlayerId = playerId;
            CurrentMapId = Config.SWARM_MATCH_MAP;
            MatchingId = matchingId;
            EnsureConnectionActive();

            Logger.LogInformation(
                "Client connection authorized: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);

            // 세션 등록은 매치 잠금 안에서 — 사람 없음 정리가 등록과 발행 사이로 끼어들 수 없다.
            MatchRuntime runtime = _matchRuntimes.GetOrCreate(matchingId);
            GameClientSession? previousSession = null;
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

                if (!Connection.TryRunIfActive(
                        () => previousSession = _registerSessionCallback(playerId, this)))
                    throw new OperationCanceledException("Connection closed before session registration.");
                registered = true;
            }
            // 이전 연결 종료는 새 연결의 상태 잠금과 매치 잠금을 벗어난 뒤 실행한다.
            if (previousSession != null)
            {
                previousSession.MarkServerInitiatedDisconnect();
                previousSession.ForceDisconnect();
            }

            // 매치 구성(사람·봇·스폰)은 manifest에서 매치당 한 번 확정한다. ticket은 신원만 증명하므로
            // 이 사람이 정말 이 매치의 참가자인지도 여기서 가른다.
            MatchComposition composition = await _matchEntry.LoadCompositionAsync(matchingId, CurrentMapId, runtime);
            EnsureConnectionActive();
            if (!composition.HumanPlayerIds.Contains(playerId))
                throw new InvalidOperationException($"Player {playerId} is not part of match {matchingId}.");
            Volatile.Write(ref _matchHumanPlayerIds, composition.HumanPlayerIds.ToArray());

            // 명단을 먼저 전송해야 이후 등장 패킷의 이름과 외형을 클라이언트가 해석할 수 있다.
            using (var rosterPacket = PacketMaker.G_TO_C_MATCH_ROSTER(matchingId, composition.PlayerRoster.ToList()))
                if (!TrySend(rosterPacket))
                    throw new OperationCanceledException("Could not send initial match roster.");

            RunUnderLiveMatch(runtime, () =>
            {
                foreach (var bot in _matchRuntimes.GetRequired(matchingId).Bots.GetBots(matchingId))
                    if (!bot.IsEliminated)
                    {
                        _gameEventLogManager.SetPlayerArea(matchingId, bot.PlayerId, bot.CurrentArea.ToString());
                    }

                // Initialize match-scoped area state once; manager implementations are idempotent.
                int matchSeed = MatchSpawnData.GetDeterministicSeed(matchingId);
                _gameEventLogManager.BeginMatch(matchingId, matchSeed);
                foreach (var bot in _matchRuntimes.GetRequired(matchingId).Bots.GetBots(matchingId))
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

                MatchStartGate.RegisterHumanPlayer(
                    matchingId, PlayerId.Value, composition.HumanPlayerIds.Count, composition.Mode);
            });

            // 초기 위치는 매치 구성의 스폰으로 정한다. PlayerInfo는 프로필 조회에만 사용한다.
            Cell matchingSpawnCell;
            {
                var playerInfo = await PlayerInfo.Load(RedisOperations, PlayerId.Value);
                if (playerInfo == null)
                    throw new InvalidOperationException($"PlayerInfo not found for authenticated player {playerId}.");

                _matchRuntimes.GetRequired(MatchingId).Roster.UpdatePlayerProfile(playerId, playerInfo.Name, playerInfo.WearItemIdList);

                matchingSpawnCell = Cell.Clone(composition.SpawnCells[playerId]);
                _lastValidatedPosition = CellToWorldPosition(matchingSpawnCell);
                _lastValidCell = Cell.Clone(matchingSpawnCell);
                _lastValidatedRotation = 0f;
                CurrentArea = GameMapData.GetCurrentArea(CurrentMapId, matchingSpawnCell);
                Logger.LogInformation(
                    "Player {PlayerId} initial Area: {Area}, Position: ({PosX:F2},{PosY:F2}), Cell: ({CellX},{CellY})",
                    PlayerId, CurrentArea, _lastValidatedPosition?.X, _lastValidatedPosition?.Y, _lastValidCell?.X,
                    _lastValidCell?.Y);
                _gameEventLogManager.LogSpawnAssignment(
                    MatchingId,
                    PlayerId.Value,
                    MatchSpawnData.GetDeterministicSeed(MatchingId),
                    MatchSpawnData.GetAnchorIndex(matchingSpawnCell),
                    matchingSpawnCell.X,
                    matchingSpawnCell.Y,
                    CurrentArea.ToString(),
                    isBot: false);
                _gameEventLogManager.SetPlayerArea(MatchingId, PlayerId.Value, CurrentArea.ToString());

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
            var connectionBoard = _matchRuntimes.GetRequired(MatchingId).Inventory.GetPlayerInventory(PlayerId.Value);
            _gameEventLogManager.LogOrbBoardTransition(
                MatchingId, PlayerId.Value, connectionBoard.GetAllItems(),
                connectionBoard.GetEquippedBattleItem()?.ItemId ?? 0, CurrentArea.ToString(), "connection_sync", isBot: false);

            // Send the initial door and mission snapshots.
            RunUnderLiveMatch(
                runtime,
                () => Doors?.Initialize(Array.Empty<AreaType>()));
            SendDoorStateList();

            // 스웜 모드(M4)는 시간 웨이브 폐쇄를 쓰므로 폐쇄 스냅샷을 복원해야 한다.
            SendAreaClosureStateSnapshot();

            // Send other players and broadcast this player's authoritative snapshot.
            await BroadcastPlayerJoin();
            EnsureConnectionActive();

            await _matchEntry.CommitAsync(
                matchingId,
                PlayerId.Value,
                composition.HumanPlayerIds);
            EnsureConnectionActive();

            // The standalone submission client is only ready after the full initial snapshot
            // has been sent. Starting the countdown earlier lets bots consume finite room stock
            // while the human client is still loading the match.
            if (composition.HumanPlayerIds.Count == 1)
            {
                MatchStartGate.MarkHumanReady(matchingId, PlayerId.Value);
            }

            // Authentication succeeds only after every fallible initialization and initial
            // snapshot step has completed. Build the complete success response before that
            // commit so serialization failure still follows the entry-abort path.
            SendMatchStartCountdown(matchingId);
            using Packet successResponse = CreateConnectResultPacket(
                true,
                ErrorCode.SUCCESS,
                "Connected to GameServer",
                matchingId,
                matchingSpawnCell);
            RunUnderLiveMatch(runtime, () =>
            {
                if (!Connection.TryMarkAuthenticated(() => Volatile.Write(ref _entryCompleted, 1)))
                    throw new OperationCanceledException("Connection closed before authentication commit.");
            });
            if (!TryPublishCommittedConnectResult(successResponse))
                return;
            Logger.LogInformation("Client connected successfully: PlayerId={PlayerId}", PlayerId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to handle connect");

            // Authentication and the local entry flag commit together. Once visible, an
            // ACK publication failure is connection-local: never roll entry back or abort
            // the whole match, and never send a contradictory negative CONNECT_RESULT.
            if (Volatile.Read(ref _entryCompleted) != 0)
            {
                CloseAfterCommittedEntryResponseFailure();
                return;
            }

            // 매치 잠금 안에서 이미 등록된 세션이다. 입장 중단 경로가 등록된 수신자 전원의 터미널
            // 에러/끊기를 한 번에 소유하므로 여기서 경쟁하는 CONNECT_RESULT를 보내지 않는다.
            if (registered)
            {
                if (!ReportEntryFailureOnce())
                {
                    MarkServerInitiatedDisconnect();
                    Connection.Disconnect();
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
            throw new OperationCanceledException("Match became terminal during game entry.");

        initialize();
    }

    private void EnsureConnectionActive()
    {
        if (Connection.IsReleased)
            throw new OperationCanceledException("Connection closed during game entry.");
    }

    private bool SendConnectResult(bool success, ErrorCode errorCode, string message,
        bool disconnectAfterSend = false)
    {
        using Packet packet = CreateConnectResultPacket(success, errorCode, message);
        return disconnectAfterSend ? Connection.TrySendAndDisconnect(packet) : Connection.TrySend(packet);
    }

    /// <summary>
    ///     Creates a complete CONNECT_RESULT frame. Successful entry calls this before the authentication commit,
    ///     keeping allocation, serialization, and body construction on the pre-commit failure side of the boundary.
    /// </summary>
    private Packet CreateConnectResultPacket(bool success, ErrorCode errorCode, string message,
        long matchingId = 0, Cell? spawnCell = null)
    {
        var response = new G_TO_C_CONNECT_RESULT
        {
            Success = success,
            ErrorCode = errorCode,
            Message = message,
            MatchingId = matchingId,
            SpawnCell = spawnCell ?? new Cell(0, 0)
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
                "Committed game entry response was not queued; closing connection: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                MatchingId);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Committed game entry response enqueue failed; closing connection: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                MatchingId);
        }

        CloseAfterCommittedEntryResponseFailure();
        return false;
    }

    /// <summary>
    ///     Closes a connection whose entry already committed without reporting an entry failure or attempting
    ///     a contradictory negative CONNECT_RESULT.
    /// </summary>
    private void CloseAfterCommittedEntryResponseFailure()
    {
        MarkServerInitiatedDisconnect();
        try
        {
            Connection.Disconnect();
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to close connection after committed entry response failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                MatchingId);
        }
    }

    private Task BroadcastPlayerJoin()
    {
        if (!PlayerId.HasValue || IsEliminated) return Task.CompletedTask;

        var sessions = _getSessionsByInstance(CurrentMapId, MatchingId)
            .Where(s => s.PlayerId.HasValue && s.PlayerId != PlayerId &&
                        !s.IsEliminated && s.CurrentArea == CurrentArea)
            .ToList();

        if (sessions.Count > 0)
        {
            using var others = PacketMaker.G_TO_C_OBJECT_INFO(sessions.Select(s => s.CaptureGameObjectInfo()).ToList());
            TrySend(others);
        }

        using (var mine = PacketMaker.G_TO_C_OBJECT_INFO([CaptureGameObjectInfo()]))
            foreach (var session in sessions) session.TrySend(mine);

        var bots = _matchRuntimes.GetRequired(MatchingId).Bots.GetBots(MatchingId)
            .Where(b => !b.IsEliminated && b.CurrentArea == CurrentArea).ToList();
        var objects = bots.Select(b => _matchRuntimes.GetRequired(MatchingId).Bots.SynthesizeGameObjectInfo(MatchingId, b.PlayerId))
            .OfType<GameObjectInfo>().ToList();
        if (objects.Count > 0)
        {
            using var packet = PacketMaker.G_TO_C_OBJECT_INFO(objects);
            TrySend(packet);
            foreach (var bot in bots)
            {
                using var appearance = PacketMaker.G_TO_C_PLAYER_APPEARANCE(
                    bot.PlayerId, BotPlayerManager.BuildBotWearItems(bot));
                TrySend(appearance);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>서버가 승인한 현재 공간 정보를 복사한다. PlayerInfo·Redis 데이터는 건드리지 않는다.</summary>
    internal GameObjectInfo CaptureGameObjectInfo()
    {
        var position = _lastValidatedPosition
            ?? throw new InvalidOperationException("Cannot publish a player before its spawn is initialized.");
        var cell = _lastValidCell ?? WorldPositionToCell(position);
        return new GameObjectInfo(ObjectType.PLAYER, PlayerId!.Value, CurrentMapId, MatchingId, cell)
        {
            Position = new Vector3f(position.X, position.Y, position.Z),
            Velocity = new Vector3f(_lastValidatedVelocity.X, _lastValidatedVelocity.Y, _lastValidatedVelocity.Z),
            Rotation = _lastValidatedRotation,
            State = _isSleeping ? PlayerState.SLEEP : CurrentState
        };
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
        TrySend(packet);
    }

    private Task HandleMatchStartReady()
    {
        if (PlayerId.HasValue && MatchingId > 0)
        {
            MatchStartGate.MarkHumanReady(MatchingId, PlayerId.Value);
            SendMatchStartCountdown(MatchingId);
        }

        return Task.CompletedTask;
    }

    private Task HandleHeartbeat()
    {
        // Send the heartbeat response.
        using var packet = PacketMaker.G_TO_C_HEART_BEAT(DateTime.UtcNow);
        TrySend(packet);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     세션의 TCP 연결을 즉시 종료한다.
    /// </summary>
    public void ForceDisconnect()
    {
        Logger.LogWarning("Force disconnecting PlayerId={PlayerId}", PlayerId);
        Connection.Disconnect();
    }

    /// <summary>
    /// 새 접속과 재접속 시 현재 방송 대상·남은 시간·누적 폐쇄 지역을 복원한다.
    /// 미래 웨이브는 아직 보내지 않아 다음 방송 전까지 대상이 드러나지 않는다.
    /// </summary>
    private void SendAreaClosureStateSnapshot()
    {
        if (MatchingId <= 0) return;

        var snapshot = _matchRuntimes.GetRequired(MatchingId).Closures.GetClientStateSnapshot();
        foreach (var closedArea in snapshot.ClosedAreas)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSED
            {
                AreaType = closedArea,
                SuppressAlert = true
            }));
            TrySend(packet);
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
            TrySend(packet);
        }
        var globalClosure = _matchRuntimes.GetRequired(MatchingId).Closures.GetGlobalClosureClientState();
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
            TrySend(packet);
        }

        // AreaType.None is the generic next-warning clock.
        using var countdownPacket = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
        countdownPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
        {
            AreaType = AreaType.None,
            SecondsRemaining = snapshot.NextWarningSeconds,
            ClosureAtUnixMs = snapshot.NextWarningAtUnixMs
        }));
        TrySend(countdownPacket);

        // #272 자기장: 수축 시계를 복원한다 — 클라 경계 렌더의 유일한 입력. 폐쇄 시계와
        // 같은 앵커(GameStartTime)라 별도 상태가 없다.
        var closureState = _matchRuntimes.GetRequired(MatchingId).Closures.GetMatchingState();
        if (Config.SWARM_PRESSURE_FIELD_ENABLED && closureState != null)
        {
            using var fieldPacket = Packet.Create((int)Protocol.G_TO_C_SWARM_FIELD_STATE);
            fieldPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_FIELD_STATE
            {
                StartedAtUnixMs = new DateTimeOffset(closureState.GameStartTime).ToUnixTimeMilliseconds()
            }));
            TrySend(fieldPacket);
        }
    }

}
