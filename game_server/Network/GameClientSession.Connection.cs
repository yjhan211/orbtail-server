using System.Collections.Generic;
using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private async Task HandleConnect(C_TO_G_CONNECT msg)
    {
        try
        {
            Logger.LogInformation("Client connection request: PlayerId={MsgPlayerId}, MatchingId={MsgMatchingId}",
                msg.PlayerId, msg.MatchingId);

            // TODO: MatchingId 검증 (Redis에서 매칭 정보 확인)
            // 지금은 간단하게 PlayerId만 설정

            PlayerId = msg.PlayerId;
            CurrentMapId = MapId.School; // TODO: 매칭 정보에서 가져오기
            CurrentMapSubId = msg.MatchingId;

            // 마니또 체인 정보 저장
            TargetPlayerId = msg.TargetPlayerId;
            MyJobTitle = msg.MyJobTitle;
            TargetJobTitle = msg.TargetJobTitle;
            Logger.LogInformation("마니또 체인: PlayerId={PlayerId}, 타겟={Target}, 내 직책={MyJob}, 타겟 직책={TargetJob}",
                PlayerId, TargetPlayerId, MyJobTitle, TargetJobTitle);

            // 체인 매니저에 링크 등록 (각 플레이어가 접속할 때마다 누적)
            _manittoChainManager.RegisterLink(msg.MatchingId, new ChainLink
            {
                PlayerId = msg.PlayerId,
                TargetPlayerId = msg.TargetPlayerId,
                MyJobTitle = msg.MyJobTitle,
                TargetJobTitle = msg.TargetJobTitle
            });

            // 미션 초기화
            _missionManager.InitializePlayer(msg.MatchingId, msg.PlayerId, msg.MyJobTitle);
            _missionManager.EnsureBroadcastTransmitterGift(msg.MatchingId, msg.PlayerId, msg.TargetPlayerId);

            // 봇 로드 (매칭당 최초 1회) — 폐쇄 초기화 전에 로드해 직책 풀을 확정 (#87)
            await LoadBotsIfNeeded(msg.MatchingId, CurrentMapId);

            // 구역 폐쇄 초기화 (매칭당 최초 1회)
            // #87: 매칭의 직책 풀을 셔플 우선순위에 반영 (5분 1단계 보장 + 직책별 후순위)
            var jobPool = _manittoChainManager.GetMatchingJobs(msg.MatchingId);
            _areaClosureManager.InitializeMatching(msg.MatchingId, jobPool);

            // 인게임 스탯 초기화
            ResetInGameStats();

            // 게임 타이머 시작 (해당 매칭에 대해 최초 1회만)
            StartGameTimerIfNeeded(msg.MatchingId);

            // 세션 등록
            _registerSessionCallback(PlayerId.Value, this);

            // 초기 위치 로드
            await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
            var playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

            if (playerInfo != null)
            {
                _lastValidatedPosition = playerInfo.ObjectInfo.Position;
                _lastValidCell = playerInfo.ObjectInfo.Cell;
                _lastValidatedRotation = playerInfo.ObjectInfo.Rotation;
                // 초기 Area 설정
                CurrentArea = GameMapData.GetCurrentArea(CurrentMapId, playerInfo.ObjectInfo.Cell);
                Logger.LogInformation(
                    "Player {PlayerId} initial Area: {Area}, Position: ({PosX:F2},{PosY:F2}), Cell: ({CellX},{CellY})",
                    PlayerId, CurrentArea, _lastValidatedPosition?.X, _lastValidatedPosition?.Y, _lastValidCell?.X,
                    _lastValidCell?.Y);

                // 초기 Area의 Interactable 목록 전송
                if (CurrentArea != AreaType.None)
                {
                    SendInteractableList(CurrentArea);
                    SendInteractCooldownSnapshot();

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

            Logger.LogInformation("Client connected successfully: PlayerId={L}", PlayerId);

            // 인게임 기본 아이템 지급
            foreach ((int itemId, int count) in GameRuleData.InGameItemList)
            {
                _inGameInventoryManager.AddItem(CurrentMapSubId, PlayerId.Value, itemId, count);
                Logger.LogInformation(
                    "InGame default item added: PlayerId={PlayerId}, ItemId={ItemId}, Count={Count}", PlayerId,
                    itemId, count);
            }

            // 인게임 인벤토리 목록 전송
            SendInGameInventoryList();

            // 문 초기 상태 설정 및 열린 문 목록 전송
            _doorStateManager.InitializeMatching(CurrentMapSubId);
            SendDoorStateList();

            // 미션 정보 전송
            SendMissionInfo();
            SendRoundStateSnapshot(msg.MatchingId);

            // 다른 플레이어들 정보 전송 & 내 정보 브로드캐스트
            await BroadcastPlayerJoin();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to handle connect");

            // 연결 실패 응답
            using var packet = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT);
            var response = new G_TO_C_CONNECT_RESULT
            {
                Success = false,
                ErrorCode = ErrorCode.FATAL,
                Message = "연결 처리 중 오류가 발생했습니다"
            };
            packet.SetBody(MessagePackSerializer.Serialize(response));
            Send(packet);
        }
    }

    private async Task BroadcastPlayerJoin()
    {
        if (!PlayerId.HasValue) return;

        try
        {
            var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            // 같은 Area의 플레이어만 필터링
            var sameAreaSessions = allSessions
                .Where(s => s.PlayerId != PlayerId && s.CurrentArea == CurrentArea && s.PlayerId.HasValue).ToList();

            // 1. 나에게 같은 Area의 다른 플레이어들 정보 전송
            if (sameAreaSessions.Count > 0)
            {
                var playerInfoList = new List<PlayerInfo>();
                foreach (var session in sameAreaSessions)
                {
                    await using var playerLock = await PlayerInfo.Lock(RedLock, session.PlayerId!.Value);
                    var playerInfo = await PlayerInfo.Load(CacheHelper, session.PlayerId!.Value);
                    if (playerInfo != null) playerInfoList.Add(playerInfo);
                }

                if (playerInfoList.Count > 0)
                {
                    using var packet = PacketMaker.G_TO_C_PLAYER_INFO(playerInfoList);
                    Send(packet);
                    Logger.LogInformation("Sent {Count} PlayerInfo in Area {Area} to PlayerId={L}",
                        playerInfoList.Count, CurrentArea, PlayerId);
                }
            }

            // 2. 내 정보 로드
            await using var myPlayerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
            var myPlayerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

            if (myPlayerInfo != null)
            {
                // 3. 같은 Area의 다른 플레이어들에게 내 정보 브로드캐스트
                using var myPacket = PacketMaker.G_TO_C_PLAYER_INFO([myPlayerInfo]);
                foreach (var session in sameAreaSessions) session.Send(myPacket);
                Logger.LogInformation("Broadcasted my PlayerInfo (PlayerId={L}) to {Count} players in Area {Area}",
                    PlayerId, sameAreaSessions.Count, CurrentArea);
            }

            // 4. #125: 같은 Area의 봇들 정보를 나에게 전송 (실제 플레이어 동등 시각화)
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
        }
    }

    private Task HandleHeartbeat()
    {
        _lastHeartbeatTime = DateTime.UtcNow;

        // 하트비트 응답 전송
        using var packet = PacketMaker.G_TO_C_HEART_BEAT(DateTime.UtcNow);
        Send(packet);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     하트비트 타임아웃 체크. 타임아웃되면 true 반환
    /// </summary>
    public bool IsHeartbeatTimedOut()
    {
        double elapsed = (DateTime.UtcNow - _lastHeartbeatTime).TotalSeconds;
        return elapsed > HeartbeatTimeoutSeconds;
    }

    /// <summary>
    ///     강제 연결 해제
    /// </summary>
    public void ForceDisconnect()
    {
        Logger.LogWarning("Force disconnecting PlayerId={PlayerId} due to heartbeat timeout", PlayerId);
        Token.Disconnect();
    }

    /// <summary>
    ///     Redis에서 봇 정보 로드 (매칭당 최초 1회).
    ///     #125: 봇 위치 초기화에 MapId가 필요하므로 인자로 전달.
    /// </summary>
    private async Task LoadBotsIfNeeded(long matchingId, MapId mapId)
    {
        try
        {
            var botData = await CacheHelper.HashGetAsync("matching_bots", matchingId);
            if (botData.IsNullOrEmpty) return;

            var botInfoList = MessagePackSerializer.Deserialize<List<BotMatchingInfo>>((byte[])botData!);
            if (_botPlayerManager.HasBots(matchingId)
                && IsSameBotChain(_botPlayerManager.GetBots(matchingId), botInfoList))
                return;

            _botPlayerManager.RegisterBots(matchingId, mapId, botInfoList);

            // 봇도 체인 매니저 + 미션 매니저에 등록 (#26: 봇 부품 회수/결합 시뮬용)
            foreach (var bot in botInfoList)
            {
                _manittoChainManager.RegisterLink(matchingId, new ChainLink
                {
                    PlayerId = bot.PlayerId,
                    TargetPlayerId = bot.TargetPlayerId,
                    MyJobTitle = bot.MyJobTitle,
                    TargetJobTitle = bot.TargetJobTitle
                });

                // 봇 부품 상태 초기화 — 자기 직책 발견 풀 기준
                _missionManager.InitializePlayer(matchingId, bot.PlayerId, bot.MyJobTitle);
                _missionManager.EnsureBroadcastTransmitterGift(matchingId, bot.PlayerId, bot.TargetPlayerId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "봇 정보 로드 실패: MatchingId={MatchingId}", matchingId);
        }
    }

    private static bool IsSameBotChain(List<BotPlayerState> existingBots, List<BotMatchingInfo> botInfoList)
    {
        if (existingBots.Count != botInfoList.Count) return false;

        var existingById = existingBots.ToDictionary(b => b.PlayerId);
        foreach (var botInfo in botInfoList)
        {
            if (!existingById.TryGetValue(botInfo.PlayerId, out var existing)) return false;
            if (existing.TargetPlayerId != botInfo.TargetPlayerId) return false;
            if (existing.MyJobTitle != botInfo.MyJobTitle) return false;
            if (existing.TargetJobTitle != botInfo.TargetJobTitle) return false;
        }

        return true;
    }

    /// <summary>
    ///     게임 타이머 시작 (매칭당 최초 1회만)
    /// </summary>
    private void StartGameTimerIfNeeded(long matchingId)
    {
        if (GameTimers.ContainsKey(matchingId))
        {
            Logger.LogDebug("Round timer already exists for MatchingId={MatchingId}", matchingId);
            return;
        }

        var state = new RoundRuntimeState
        {
            RoundNumber = 1,
            Phase = RoundPhase.Action,
            PhaseDurationSeconds = Config.ROUND_ACTION_SECONDS,
            PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(Config.ROUND_ACTION_SECONDS),
            SettlementEliminationApplied = false,
            IsSessionEnded = false
        };
        GameRoundStates.TryAdd(matchingId, state);

        Logger.LogInformation(
            "Round session started: MatchingId={MatchingId}, Rounds={Rounds}, Action={ActionSeconds}s, Settlement={SettlementSeconds}s",
            matchingId, Config.ROUND_TOTAL_COUNT, Config.ROUND_ACTION_SECONDS, Config.ROUND_SETTLEMENT_SECONDS);

        var timer = new Timer(_ => ProcessRoundTimerTick(matchingId), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        if (!GameTimers.TryAdd(matchingId, timer))
        {
            timer.Dispose();
            GameRoundStates.TryRemove(matchingId, out _);
        }
    }

    /// <summary>
    ///     시간 초과로 게임 종료. 생존자 중 자원 총합 최대인 플레이어가 승리.
    /// </summary>
    private void ProcessRoundTimerTick(long matchingId)
    {
        if (!GameRoundStates.TryGetValue(matchingId, out var state))
            return;

        lock (state.SyncRoot)
        {
            if (state.IsSessionEnded)
                return;

            if (DateTime.UtcNow >= state.PhaseEndsAtUtc)
            {
                if (state.Phase == RoundPhase.Action)
                    EnterSettlementPhase(matchingId, state);
                else if (state.Phase == RoundPhase.Settlement)
                    AdvanceRoundOrEnd(matchingId, state);
            }

            BroadcastRoundState(matchingId, state);
        }
    }

    private void EnterSettlementPhase(long matchingId, RoundRuntimeState state)
    {
        state.Phase = RoundPhase.Settlement;
        state.PhaseDurationSeconds = Config.ROUND_SETTLEMENT_SECONDS;
        state.PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(Config.ROUND_SETTLEMENT_SECONDS);

        if (state.SettlementEliminationApplied)
            return;

        state.SettlementEliminationApplied = true;
        long? eliminatedPlayerId = SelectRoundEliminationCandidate(matchingId);
        if (eliminatedPlayerId.HasValue)
        {
            Logger.LogInformation(
                "Round settlement elimination: MatchingId={MatchingId}, Round={Round}, PlayerId={PlayerId}",
                matchingId, state.RoundNumber, eliminatedPlayerId.Value);
            ProcessRoundElimination(matchingId, eliminatedPlayerId.Value, EliminationReason.MENTAL_ZERO);
            return;
        }

        Logger.LogWarning("Round settlement entered with no elimination candidate: MatchingId={MatchingId}, Round={Round}",
            matchingId, state.RoundNumber);
    }

    private void AdvanceRoundOrEnd(long matchingId, RoundRuntimeState state)
    {
        var (isGameOver, winnerId) = _manittoChainManager.CheckGameOver(matchingId);
        if (state.RoundNumber >= Config.ROUND_TOTAL_COUNT || isGameOver)
        {
            state.Phase = RoundPhase.Ended;
            state.PhaseDurationSeconds = 0;
            state.PhaseEndsAtUtc = DateTime.UtcNow;
            state.IsSessionEnded = true;
            BroadcastRoundState(matchingId, state);

            if (DevFlags.DisableGameEnd)
            {
                Logger.LogWarning("[DEV] Game end blocked (DISABLE_GAME_END=1): Round completion matchingId={MatchingId}",
                    matchingId);
                CleanupRoundTimer(matchingId);
                return;
            }

            EndGameByRoundCompletion(matchingId, winnerId);
            return;
        }

        state.RoundNumber++;
        state.Phase = RoundPhase.Action;
        state.PhaseDurationSeconds = Config.ROUND_ACTION_SECONDS;
        state.PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(Config.ROUND_ACTION_SECONDS);
        state.SettlementEliminationApplied = false;

        Logger.LogInformation("Round advanced: MatchingId={MatchingId}, Round={Round}", matchingId, state.RoundNumber);
    }

    private long? SelectRoundEliminationCandidate(long matchingId)
    {
        var sessions = _getSessionsByInstance(CurrentMapId, matchingId);
        var botCandidates = new List<(long PlayerId, int Score)>();
        var playerCandidates = new List<(long PlayerId, int Score)>();

        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue || session.IsEliminated)
                continue;
            if (session.ManittoStatus == ManittoStatus.SPECTATING)
                continue;

            playerCandidates.Add((session.PlayerId.Value, session.Stamina + (MaxCorruption - session.Corruption)));
        }

        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            if (bot.IsEliminated || bot.ManittoStatus == ManittoStatus.SPECTATING)
                continue;

            botCandidates.Add((bot.PlayerId, bot.Stamina + (MaxCorruption - bot.Corruption)));
        }

        var candidates = botCandidates.Count > 0 ? botCandidates : playerCandidates;
        return candidates
            .OrderBy(c => c.Score)
            .ThenBy(c => c.PlayerId)
            .Select(c => (long?)c.PlayerId)
            .FirstOrDefault();
    }

    private void ProcessRoundElimination(long matchingId, long eliminatedPlayerId, EliminationReason reason)
    {
        if (_botPlayerManager.GetBot(matchingId, eliminatedPlayerId) != null)
        {
            ProcessBotRoundElimination(matchingId, eliminatedPlayerId, reason);
            return;
        }

        _ = ProcessElimination(eliminatedPlayerId, reason, deferGameOver: true);
    }

    private void ProcessBotRoundElimination(long matchingId, long botId, EliminationReason reason)
    {
        _gameEventLogManager.LogElimination(matchingId, botId, reason.ToString(), isBot: true);
        var affected = _manittoChainManager.EliminatePlayer(matchingId, botId, reason);
        var matchingSessions = _getSessionsByInstance(CurrentMapId, matchingId);

        using (var eliminatedPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED))
        {
            var eliminatedMsg = new G_TO_C_PLAYER_ELIMINATED { PlayerId = botId, Reason = reason };
            eliminatedPacket.SetBody(MessagePackSerializer.Serialize(eliminatedMsg));
            foreach (var session in matchingSessions) session.Send(eliminatedPacket);
        }

        foreach (var (affectedId, newStatus) in affected)
        {
            var session = matchingSessions.FirstOrDefault(s => s.PlayerId == affectedId);
            if (session != null)
            {
                session.ApplyChainBreakStatus(newStatus, botId);
                continue;
            }

            var bot = _botPlayerManager.GetBot(matchingId, affectedId);
            if (bot == null) continue;
            if (newStatus == ManittoStatus.ELIMINATED)
            {
                bot.IsEliminated = true;
                bot.ManittoStatus = ManittoStatus.SPECTATING;
            }
            else
            {
                bot.ManittoStatus = newStatus;
            }
        }

        foreach (var (affectedId, newStatus) in affected)
        {
            if (newStatus != ManittoStatus.TERMINAL) continue;
            _missionManager.NotifyTargetLost(matchingId, affectedId, botId, reason);
        }
    }

    private void EndGameByRoundCompletion(long matchingId, long? knownWinnerId = null)
    {
        var sessions = _getSessionsByInstance(CurrentMapId, matchingId);
        long? winnerId = knownWinnerId;

        if (!winnerId.HasValue)
        {
            winnerId = _manittoChainManager.DetermineWinnerByResources(matchingId, playerId =>
            {
                var session = sessions.FirstOrDefault(s => s.PlayerId == playerId);
                if (session != null) return (session.Stamina, session.Corruption, MaxCorruption);

                var bot = _botPlayerManager.GetBot(matchingId, playerId);
                return bot != null ? (bot.Stamina, bot.Corruption, MaxCorruption) : (0, MaxCorruption, MaxCorruption);
            });
        }

        Logger.LogInformation("Round session completed: MatchingId={MatchingId}, WinnerId={WinnerId}",
            matchingId, winnerId);

        SendGameResult(sessions, winnerId ?? 0, false, matchingId);
    }

    private void BroadcastRoundState(long matchingId, RoundRuntimeState state)
    {
        var sessions = _getSessionsByInstance(CurrentMapId, matchingId);
        if (sessions.Count == 0) return;

        using var packet = CreateRoundStatePacket(matchingId, state);
        foreach (var session in sessions) session.Send(packet);
    }

    private void SendRoundStateSnapshot(long matchingId)
    {
        if (!GameRoundStates.TryGetValue(matchingId, out var state))
            return;

        lock (state.SyncRoot)
        {
            using var packet = CreateRoundStatePacket(matchingId, state);
            Send(packet);
        }
    }

    private Packet CreateRoundStatePacket(long matchingId, RoundRuntimeState state)
    {
        return PacketMaker.G_TO_C_ROUND_STATE(
            matchingId,
            state.RoundNumber,
            Config.ROUND_TOTAL_COUNT,
            state.Phase,
            GetRoundRemainingSeconds(state),
            state.PhaseDurationSeconds,
            state.IsSessionEnded);
    }

    private static int GetRoundRemainingSeconds(RoundRuntimeState state)
    {
        if (state.IsSessionEnded || state.Phase == RoundPhase.Ended)
            return 0;

        return Math.Max(0, (int)Math.Ceiling((state.PhaseEndsAtUtc - DateTime.UtcNow).TotalSeconds));
    }

    private static void CleanupRoundTimer(long matchingId)
    {
        GameRoundStates.TryRemove(matchingId, out _);
        if (GameTimers.TryRemove(matchingId, out var timer))
            timer.Dispose();
    }

    private void EndGameByTimeout(long matchingId)
    {
        if (DevFlags.DisableGameEnd)
        {
            Logger.LogWarning("[DEV] 게임 종료 차단됨 (DISABLE_GAME_END=1): EndGameByTimeout matchingId={MatchingId}",
                matchingId);
            // 타이머는 정리 (재발화 방지)
            if (GameTimers.TryRemove(matchingId, out var t)) t.Dispose();
            return;
        }

        Logger.LogInformation("게임 시간 초과: MatchingId={MatchingId}", matchingId);

        // 타이머 정리
        if (GameTimers.TryRemove(matchingId, out var timer))
            timer.Dispose();

        var sessions = _getSessionsByInstance(CurrentMapId, matchingId);

        // 승자 판정: 자원 총합 최대 (봇 포함)
        long? winnerId = _manittoChainManager.DetermineWinnerByResources(matchingId, playerId =>
        {
            var s = sessions.FirstOrDefault(s => s.PlayerId == playerId);
            if (s != null) return (s.Stamina, s.Corruption, MaxCorruption);

            var bot = _botPlayerManager.GetBot(matchingId, playerId);
            return bot != null ? (bot.Stamina, bot.Corruption, 100) : (0, 100, 100);
        });

        Logger.LogInformation("시간 초과 승자: MatchingId={MatchingId}, WinnerId={WinnerId}", matchingId, winnerId);

        // 결과 패킷 전송
        SendGameResult(sessions, winnerId ?? 0, true, matchingId);
    }

}
