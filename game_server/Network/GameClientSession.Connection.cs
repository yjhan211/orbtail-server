using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.interfaces;
using network.packets;
using network.routing;
using network.utils;

namespace game_server.network;

public partial class GameClientSession
{
    private async Task HandleConnect(C_TO_G_CONNECT msg)
    {
        try
        {
            _logger.LogInformation("Client connection request: PlayerId={MsgPlayerId}, MatchingId={MsgMatchingId}", msg.PlayerId, msg.MatchingId);

            // TODO: MatchingId 검증 (Redis에서 매칭 정보 확인)
            // 지금은 간단하게 PlayerId만 설정

            PlayerId = msg.PlayerId;
            CurrentMapId = MapId.School; // TODO: 매칭 정보에서 가져오기
            CurrentMapSubId = msg.MatchingId;

            // 인게임 스탯 초기화
            ResetInGameStats();

            // 게임 타이머 시작 (해당 매칭에 대해 최초 1회만)
            StartGameTimerIfNeeded(msg.MatchingId);

            // 세션 등록
            _registerSessionCallback(PlayerId.Value, this);

            // 초기 위치 로드
            await using var playerLock = await PlayerInfo.Lock(_redLock, PlayerId.Value);
            var playerInfo = await PlayerInfo.Load(_cacheHelper, PlayerId.Value);

            if (playerInfo != null)
            {
                _lastValidatedPosition = playerInfo.ObjectInfo.Position;
                _lastValidCell = playerInfo.ObjectInfo.Cell;
                // 초기 Area 설정
                CurrentArea = GameMapData.GetCurrentArea(CurrentMapId, playerInfo.ObjectInfo.Cell);
                _logger.LogInformation("Player {PlayerId} initial Area: {Area}, Position: ({PosX:F2},{PosY:F2}), Cell: ({CellX},{CellY})",
                    PlayerId, CurrentArea, _lastValidatedPosition?.X, _lastValidatedPosition?.Y, _lastValidCell?.X, _lastValidCell?.Y);

                // 초기 Area의 Interactable 목록 전송
                if (CurrentArea != AreaType.None)
                {
                    SendInteractableList(CurrentArea);

                    // 초기 Area에서도 사보타주 이벤트 트리거
                    _sabotageManager.OnPlayerEnterArea(CurrentMapSubId, CurrentArea);
                }
            }

            // 연결 성공 응답
            using var connectResultPacket = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT, PlayerId.Value);
            var response = new G_TO_C_CONNECT_RESULT
            {
                Success = true,
                ErrorCode = ErrorCode.SUCCESS,
                Message = "Connected to GameServer"
            };
            connectResultPacket.SetBody(MessagePackSerializer.Serialize(response));
            Send(connectResultPacket);

            _logger.LogInformation("Client connected successfully: PlayerId={L}", PlayerId);

            // 인게임 기본 아이템 지급
            if (GameRuleData.InGameItemList != null)
            {
                foreach (var (itemId, count) in GameRuleData.InGameItemList)
                {
                    _inGameInventoryManager.AddItem(CurrentMapSubId, PlayerId.Value, itemId, count);
                    _logger.LogInformation("InGame default item added: PlayerId={PlayerId}, ItemId={ItemId}, Count={Count}", PlayerId, itemId, count);
                }
            }

            // 인게임 인벤토리 목록 전송
            SendInGameInventoryList();

            // 탈출 절차 정보 전송
            SendExitStepInfo();

            // 문 초기 상태 설정 및 열린 문 목록 전송
            _doorStateManager.InitializeMatching(CurrentMapSubId);
            SendDoorStateList();

            // 복도 종소리 스케줄 전송
            SendCorridorBellSchedule();

            // 다른 플레이어들 정보 전송 & 내 정보 브로드캐스트
            await BroadcastPlayerJoin();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle connect");

            // 연결 실패 응답
            using var packet = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT);
            var response = new G_TO_C_CONNECT_RESULT
            {
                Success = false,
                ErrorCode = ErrorCode.FATAL,
                Message = ex.Message
            };
            packet.SetBody(MessagePackSerializer.Serialize(response));
            Send(packet);
        }
    }

    private async Task BroadcastPlayerJoin()
    {
        if (!PlayerId.HasValue)
        {
            return;
        }

        try
        {
            var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            // 같은 Area의 플레이어만 필터링
            var sameAreaSessions = allSessions.Where(s => s.PlayerId != PlayerId && s.CurrentArea == CurrentArea && s.PlayerId.HasValue).ToList();

            // 1. 나에게 같은 Area의 다른 플레이어들 정보 전송
            if (sameAreaSessions.Count > 0)
            {
                var playerInfoList = new List<PlayerInfo>();
                foreach (var session in sameAreaSessions)
                {
                    await using var playerLock = await PlayerInfo.Lock(_redLock, session.PlayerId!.Value);
                    var playerInfo = await PlayerInfo.Load(_cacheHelper, session.PlayerId!.Value);
                    if (playerInfo != null)
                    {
                        playerInfoList.Add(playerInfo);
                    }
                }

                if (playerInfoList.Count > 0)
                {
                    using var packet = PacketMaker.G_TO_C_PLAYER_INFO(playerInfoList);
                    Send(packet);
                    _logger.LogInformation("Sent {Count} PlayerInfo in Area {Area} to PlayerId={L}", playerInfoList.Count, CurrentArea, PlayerId);
                }
            }

            // 2. 내 정보 로드
            await using var myPlayerLock = await PlayerInfo.Lock(_redLock, PlayerId.Value);
            var myPlayerInfo = await PlayerInfo.Load(_cacheHelper, PlayerId.Value);

            if (myPlayerInfo != null)
            {
                // 3. 같은 Area의 다른 플레이어들에게 내 정보 브로드캐스트
                using var myPacket = PacketMaker.G_TO_C_PLAYER_INFO([myPlayerInfo]);
                foreach (var session in sameAreaSessions)
                {
                    session.Send(myPacket);
                }
                _logger.LogInformation("Broadcasted my PlayerInfo (PlayerId={L}) to {Count} players in Area {Area}", PlayerId, sameAreaSessions.Count, CurrentArea);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast player join for PlayerId={L}", PlayerId);
        }
    }

    private Task HandleHeartbeat()
    {
        _lastHeartbeatTime = DateTime.UtcNow;

        // 하트비트 응답 전송
        var serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var packet = PacketMaker.G_TO_C_HEART_BEAT(DateTime.UtcNow);
        Send(packet);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 하트비트 타임아웃 체크. 타임아웃되면 true 반환
    /// </summary>
    public bool IsHeartbeatTimedOut()
    {
        var elapsed = (DateTime.UtcNow - _lastHeartbeatTime).TotalSeconds;
        return elapsed > HeartbeatTimeoutSeconds;
    }

    /// <summary>
    /// 강제 연결 해제
    /// </summary>
    public void ForceDisconnect()
    {
        _logger.LogWarning("Force disconnecting PlayerId={PlayerId} due to heartbeat timeout", PlayerId);
        _token.Disconnect();
    }

    /// <summary>
    /// 게임 타이머 시작 (매칭당 최초 1회만)
    /// </summary>
    private void StartGameTimerIfNeeded(long matchingId)
    {
        lock (_timerLock)
        {
            if (_gameTimers.ContainsKey(matchingId))
            {
                _logger.LogDebug("Game timer already exists for MatchingId={MatchingId}", matchingId);
                return;
            }

            _logger.LogInformation("게임 타이머 시작: MatchingId={MatchingId} ({Minutes}분)", matchingId, GameDurationMinutes);

            var timer = new Timer(_ =>
            {
                EndGameByTimeout(matchingId);
            }, null, TimeSpan.FromSeconds(GameDurationSeconds), Timeout.InfiniteTimeSpan);

            _gameTimers[matchingId] = timer;
        }
    }

    /// <summary>
    /// 시간 초과로 게임 종료
    /// </summary>
    private void EndGameByTimeout(long matchingId)
    {
        _logger.LogInformation("게임 시간 초과: MatchingId={MatchingId}, 타이머 시작 세션 PlayerId={PlayerId}, CurrentMapSubId={CurrentMapSubId}",
            matchingId, PlayerId, CurrentMapSubId);

        // 타이머 정리
        lock (_timerLock)
        {
            if (_gameTimers.TryGetValue(matchingId, out var timer))
            {
                timer.Dispose();
                _gameTimers.Remove(matchingId);
            }
        }

        // 해당 매칭의 모든 플레이어에게 게임 종료 패킷 전송
        var sessions = _getSessionsByInstance(CurrentMapId, matchingId);
        _logger.LogInformation("게임 종료 패킷 전송 대상: MatchingId={MatchingId}, 필터(MapId={MapId}, MapSubId={MapSubId}), 대상 세션 수={Count}",
            matchingId, CurrentMapId, matchingId, sessions.Count);

        using var packet = PacketMaker.G_TO_C_GAME_END(matchingId, isEscaped: false);

        foreach (var session in sessions)
        {
            _logger.LogInformation("게임 종료 패킷 전송: PlayerId={PlayerId}, 세션의 CurrentMapSubId={SessionMapSubId}",
                session.PlayerId, session.CurrentMapSubId);
            session.Send(packet);
        }
    }

    /// <summary>
    /// 매칭 종료 시 타이머 정리
    /// </summary>
    public static void CleanupGameTimer(long matchingId)
    {
        lock (_timerLock)
        {
            if (_gameTimers.TryGetValue(matchingId, out var timer))
            {
                timer.Dispose();
                _gameTimers.Remove(matchingId);
            }
        }
    }
}
