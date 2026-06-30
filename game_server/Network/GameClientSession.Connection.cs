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
            foreach (var bot in _botPlayerManager.GetBots(msg.MatchingId))
                if (!bot.IsEliminated)
                    _presenceTracker?.SetPlayerArea(msg.MatchingId, bot.PlayerId, bot.CurrentArea,
                        countAsEntry: false);

            // 구역 폐쇄 초기화 (매칭당 최초 1회)
            // #87: 매칭의 직책 풀을 셔플 우선순위에 반영 (5분 1단계 보장 + 직책별 후순위)
            var jobPool = _manittoChainManager.GetMatchingJobs(msg.MatchingId);
            _areaClosureManager.InitializeMatching(msg.MatchingId, jobPool);

            // 인게임 스탯 초기화
            ResetInGameStats();

            // 세션 등록 후 게임 타이머 시작 (해당 매칭에 대해 최초 1회만)
            _registerSessionCallback(PlayerId.Value, this);

            StartGameTimerIfNeeded(msg.MatchingId);

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
                _presenceTracker?.SetPlayerArea(CurrentMapSubId, PlayerId.Value, CurrentArea,
                    countAsEntry: false);

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
            SendChecklistInfo();

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
        lock (_roundSessionStartLock)
        {
            bool hasOtherActiveSession = HasOtherActiveHumanSession(matchingId);
            if (GameTimers.TryGetValue(matchingId, out var existingTimer))
            {
                if (hasOtherActiveSession)
                {
                    Logger.LogDebug("Round timer already exists for MatchingId={MatchingId}", matchingId);
                    return;
                }

                Logger.LogWarning(
                    "Resetting stale round timer before starting new session: MatchingId={MatchingId}",
                    matchingId);
                if (GameTimers.TryRemove(matchingId, out var staleTimer))
                    staleTimer.Dispose();
                else
                    existingTimer.Dispose();
            }

            if (GameRoundStates.TryRemove(matchingId, out var staleState))
            {
                Logger.LogWarning(
                    "Resetting stale round state before starting new session: MatchingId={MatchingId}, OldRound={Round}, OldPhase={Phase}",
                    matchingId, staleState.RoundNumber, staleState.Phase);
            }

            _checklistManager.RemoveMatchingState(matchingId);
            _presenceTracker?.Remove(matchingId);

            var state = new RoundRuntimeState
            {
                RoundNumber = 1,
                Phase = RoundPhase.Action,
                PhaseDurationSeconds = Config.ROUND_ACTION_SECONDS,
                PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(Config.ROUND_ACTION_SECONDS),
                SettlementEliminationApplied = false,
                IsSessionEnded = false
            };
            GameRoundStates[matchingId] = state;
            StartChecklistRound(matchingId, state, broadcast: false);

            Logger.LogInformation(
                "Round session started: MatchingId={MatchingId}, Rounds={Rounds}, Action={ActionSeconds}s, Settlement={SettlementSeconds}s",
                matchingId, Config.ROUND_TOTAL_COUNT, Config.ROUND_ACTION_SECONDS, Config.ROUND_SETTLEMENT_SECONDS);

            var timer = new Timer(_ => ProcessRoundTimerTick(matchingId), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

            if (!GameTimers.TryAdd(matchingId, timer))
            {
                timer.Dispose();
                GameRoundStates.TryRemove(matchingId, out _);
                _checklistManager.RemoveMatchingState(matchingId);
            }
        }
    }

    private bool HasOtherActiveHumanSession(long matchingId)
    {
        return _getSessionsByInstance(CurrentMapId, matchingId)
            .Any(session =>
                !ReferenceEquals(session, this)
                && session.PlayerId.HasValue
                && !session.IsEliminated
                && !session._isGameEnded
                && !session._isServerInitiatedDisconnect);
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
                AdvanceRoundPhase(matchingId, state);

            BroadcastRoundState(matchingId, state);
        }
    }

    private void EnterSettlementPhase(long matchingId, RoundRuntimeState state)
    {
        state.Phase = RoundPhase.SettlementNomination;
        state.PhaseDurationSeconds = Config.ROUND_SETTLEMENT_NOMINATION_SECONDS;
        state.PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(Config.ROUND_SETTLEMENT_NOMINATION_SECONDS);
        state.SettlementNominations.Clear();
        state.BotNominationsInjected = false;
        ClearSettlementContributionResult(state);
        state.SettlementEliminationApplied = false;
        _presenceTracker?.FreezeNotebookOverlaps(matchingId);
        BroadcastPresenceNotebookUpdates(matchingId, state.RoundNumber);
    }

    private void AdvanceRoundPhase(long matchingId, RoundRuntimeState state)
    {
        switch (state.Phase)
        {
            case RoundPhase.Action:
                EnterSettlementPhase(matchingId, state);
                break;
            case RoundPhase.SettlementNomination:
                EnterSettlementResultPhase(matchingId, state);
                break;
            case RoundPhase.SettlementResult:
                EnterSettlementContributionPhase(matchingId, state);
                break;
            case RoundPhase.SettlementContributionReveal:
                EnterSettlementSubPhase(state, RoundPhase.SettlementDetectionResultReveal,
                    Config.ROUND_SETTLEMENT_DETECTION_RESULT_SECONDS);
                break;
            case RoundPhase.SettlementDetectionResultReveal:
                EnterSettlementSubPhase(state, RoundPhase.SettlementEliminationReveal,
                    Config.ROUND_SETTLEMENT_ELIMINATION_SECONDS);
                break;
            case RoundPhase.SettlementEliminationReveal:
                ApplySettlementElimination(matchingId, state);
                AdvanceRoundOrEnd(matchingId, state);
                break;
        }
    }

    private void EnterSettlementResultPhase(long matchingId, RoundRuntimeState state)
    {
        InjectBotPresenceSettlementNominations(matchingId, state);
        state.Phase = RoundPhase.SettlementResult;
        state.PhaseDurationSeconds = Config.ROUND_SETTLEMENT_RESULT_SECONDS;
        state.PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(Config.ROUND_SETTLEMENT_RESULT_SECONDS);
        BroadcastSettlementNominationResults(matchingId, state);
    }

    private static void EnterSettlementSubPhase(RoundRuntimeState state, RoundPhase phase, int durationSeconds)
    {
        state.Phase = phase;
        state.PhaseDurationSeconds = Math.Max(1, durationSeconds);
        state.PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(state.PhaseDurationSeconds);
    }

    private void EnterSettlementContributionPhase(long matchingId, RoundRuntimeState state)
    {
        EnterSettlementSubPhase(state, RoundPhase.SettlementContributionReveal,
            Config.ROUND_SETTLEMENT_CONTRIBUTION_SECONDS);
        BuildSettlementContributionResult(matchingId, state);
        BroadcastRoundState(matchingId, state);
        BroadcastSettlementContributionResults(matchingId, state);
    }

    private void InjectBotPresenceSettlementNominations(long matchingId, RoundRuntimeState state)
    {
        if (state.BotNominationsInjected)
            return;

        state.BotNominationsInjected = true;
        var roster = GetSettlementActivePlayerIds(matchingId);
        if (roster.Count <= 1)
            return;

        var bots = _botPlayerManager.GetBots(matchingId)
            .Where(bot => !bot.IsEliminated && bot.ManittoStatus != ManittoStatus.SPECTATING)
            .OrderBy(bot => bot.PlayerId)
            .ToList();
        if (bots.Count == 0)
            return;

        foreach (var bot in bots)
        {
            if (state.SettlementNominations.ContainsKey(bot.PlayerId))
                continue;

            var candidates = _presenceTracker?
                .GetNominationCandidates(matchingId, bot.PlayerId, roster, bot.TargetPlayerId)
                .ToList();

            if (candidates is not { Count: > 0 })
            {
                long fallbackTargetPlayerId = ResolveFallbackBotNominationTarget(roster, bot.PlayerId, bot.TargetPlayerId);
                if (fallbackTargetPlayerId == 0)
                {
                    Logger.LogDebug(
                        "Settlement bot nomination skipped: MatchingId={MatchingId}, Round={Round}, Bot={BotId}, no nomination candidate",
                        matchingId, state.RoundNumber, bot.PlayerId);
                    continue;
                }

                state.SettlementNominations[bot.PlayerId] = fallbackTargetPlayerId;
                Logger.LogInformation(
                    "Settlement bot nomination by fallback: MatchingId={MatchingId}, Round={Round}, Bot={BotId}, Target={TargetId}",
                    matchingId, state.RoundNumber, bot.PlayerId, fallbackTargetPlayerId);
                continue;
            }

            var selected = candidates[0];
            state.SettlementNominations[bot.PlayerId] = selected.CandidateId;
            Logger.LogInformation(
                "Settlement bot nomination by suspicion: MatchingId={MatchingId}, Round={Round}, Bot={BotId}, Target={TargetId}, Score={Score}, Presence={Presence}, TotalOverlap={TotalOverlap}, LongestOverlap={LongestOverlap}, FollowEntries={FollowEntries}, OverlapStarts={OverlapStarts}, LastSeenArea={LastSeenArea}",
                matchingId, state.RoundNumber, bot.PlayerId, selected.CandidateId, selected.Score, selected.Presence,
                selected.TotalOverlapSeconds, selected.LongestOverlapSeconds, selected.EnterAfterObserverCount,
                selected.OverlapStartCount, selected.LastSeenArea);
        }
    }

    private static long ResolveFallbackBotNominationTarget(
        IEnumerable<long> roster, long botPlayerId, long botTargetPlayerId)
    {
        return roster
            .Where(playerId => playerId != botPlayerId && playerId != botTargetPlayerId)
            .OrderBy(playerId => playerId)
            .FirstOrDefault();
    }

    private void BroadcastSettlementNominationResults(long matchingId, RoundRuntimeState state)
    {
        var sessions = _getSessionsByInstance(CurrentMapId, matchingId);
        if (sessions.Count == 0) return;

        var nominations = state.SettlementNominations
            .Select(pair => new SettlementNominationEntry
            {
                NominatorPlayerId = pair.Key,
                TargetPlayerId = pair.Value
            })
            .ToList();

        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue)
                continue;

            long targetPlayerId = session.PlayerId.Value;
            var nominators = nominations
                .Where(entry => entry.TargetPlayerId == targetPlayerId)
                .Select(entry => entry.NominatorPlayerId)
                .ToList();

            using var packet = Packet.Create((int)Protocol.G_TO_C_SETTLEMENT_NOMINATION_RESULT, targetPlayerId);
            var msg = new G_TO_C_SETTLEMENT_NOMINATION_RESULT
            {
                MatchingId = matchingId,
                RoundNumber = state.RoundNumber,
                TargetPlayerId = targetPlayerId,
                NominatorPlayerIds = nominators,
                NominatedByCount = nominators.Count,
                Nominations = nominations
            };
            packet.SetBody(MessagePackSerializer.Serialize(msg));
            session.Send(packet);
        }
    }

    private void BuildSettlementContributionResult(long matchingId, RoundRuntimeState state)
    {
        ClearSettlementContributionResult(state);

        var playerIds = GetSettlementActivePlayerIds(matchingId);
        if (playerIds.Count == 0)
            return;

        var checklistEntries = _checklistManager.BuildSettlementContributionEntries(matchingId, playerIds);
        if (checklistEntries.Any(entry => entry.Contribution > 0))
        {
            state.SettlementContributionEntries.AddRange(checklistEntries);
        }
        else
        {
            foreach (var entry in BuildFallbackSettlementContributionEntries(playerIds))
            {
                state.SettlementContributionEntries.Add(entry);
            }
        }

        var top = state.SettlementContributionEntries
            .OrderByDescending(entry => entry.Contribution)
            .ThenBy(entry => entry.PlayerId)
            .First();
        var lowest = state.SettlementContributionEntries
            .OrderBy(entry => entry.Contribution)
            .ThenBy(entry => entry.PlayerId)
            .First();

        state.SettlementContributionTopPlayerId = top.PlayerId;
        state.SettlementContributionLowestPlayerId = lowest.PlayerId;
        state.SettlementContributionTopValue = top.Contribution;
        state.SettlementContributionLowestValue = lowest.Contribution;

        long decisiveTargetPlayerId = state.SettlementNominations.TryGetValue(top.PlayerId, out long nominatedTarget)
            ? nominatedTarget
            : 0;
        bool success = decisiveTargetPlayerId != 0
                       && _manittoChainManager.IsAliveManittoOf(matchingId, top.PlayerId, decisiveTargetPlayerId);

        state.SettlementContributionDecisiveTargetPlayerId = decisiveTargetPlayerId;
        state.SettlementContributionNominationSuccess = success;
        state.SettlementContributionEliminatedPlayerId = success ? decisiveTargetPlayerId : lowest.PlayerId;

        Logger.LogInformation(
            "Settlement contribution result: MatchingId={MatchingId}, Round={Round}, Top={TopPlayerId}:{TopValue}, Lowest={LowestPlayerId}:{LowestValue}, Success={Success}, Eliminated={EliminatedPlayerId}",
            matchingId, state.RoundNumber, state.SettlementContributionTopPlayerId,
            state.SettlementContributionTopValue, state.SettlementContributionLowestPlayerId,
            state.SettlementContributionLowestValue, success, state.SettlementContributionEliminatedPlayerId);
    }

    private static List<SettlementContributionEntry> BuildFallbackSettlementContributionEntries(List<long> playerIds)
    {
        var entries = new List<SettlementContributionEntry>();
        var contributionPool = new List<int> { 9, 21, 27, 21, 15 };
        ShuffleSettlementContributionPool(contributionPool);

        for (int i = 0; i < playerIds.Count; i++)
        {
            if (i > 0 && i % contributionPool.Count == 0)
                ShuffleSettlementContributionPool(contributionPool);

            entries.Add(new SettlementContributionEntry
            {
                PlayerId = playerIds[i],
                Contribution = contributionPool[i % contributionPool.Count]
            });
        }

        return entries;
    }

    private static void ShuffleSettlementContributionPool(List<int> values)
    {
        for (int i = values.Count - 1; i > 0; i--)
        {
            int swapIndex = Random.Shared.Next(i + 1);
            (values[i], values[swapIndex]) = (values[swapIndex], values[i]);
        }
    }

    private List<long> GetSettlementActivePlayerIds(long matchingId)
    {
        var result = new List<long>();

        foreach (var session in _getSessionsByInstance(CurrentMapId, matchingId))
        {
            if (!session.PlayerId.HasValue || session.IsEliminated ||
                session.ManittoStatus == ManittoStatus.SPECTATING)
                continue;

            result.Add(session.PlayerId.Value);
        }

        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            if (bot.IsEliminated || bot.ManittoStatus == ManittoStatus.SPECTATING)
                continue;

            result.Add(bot.PlayerId);
        }

        return result
            .Distinct()
            .OrderBy(playerId => playerId)
            .ToList();
    }

    private void BroadcastSettlementContributionResults(long matchingId, RoundRuntimeState state)
    {
        var sessions = _getSessionsByInstance(CurrentMapId, matchingId);
        if (sessions.Count == 0 || state.SettlementContributionEntries.Count == 0) return;

        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue)
                continue;

            using var packet = Packet.Create((int)Protocol.G_TO_C_SETTLEMENT_CONTRIBUTION_RESULT,
                session.PlayerId.Value);
            var msg = new G_TO_C_SETTLEMENT_CONTRIBUTION_RESULT
            {
                MatchingId = matchingId,
                RoundNumber = state.RoundNumber,
                Entries = state.SettlementContributionEntries
                    .Select(entry => new SettlementContributionEntry
                    {
                        PlayerId = entry.PlayerId,
                        Contribution = entry.Contribution
                    })
                    .ToList(),
                TopPlayerId = state.SettlementContributionTopPlayerId,
                LowestPlayerId = state.SettlementContributionLowestPlayerId,
                TopContribution = state.SettlementContributionTopValue,
                LowestContribution = state.SettlementContributionLowestValue,
                DecisivePlayerId = state.SettlementContributionTopPlayerId,
                DecisiveTargetPlayerId = state.SettlementContributionDecisiveTargetPlayerId,
                IsNominationSuccess = state.SettlementContributionNominationSuccess,
                EliminatedPlayerId = state.SettlementContributionEliminatedPlayerId
            };
            packet.SetBody(MessagePackSerializer.Serialize(msg));
            session.Send(packet);
        }
    }

    private static void ClearSettlementContributionResult(RoundRuntimeState state)
    {
        state.SettlementContributionEntries.Clear();
        state.SettlementContributionTopPlayerId = 0;
        state.SettlementContributionLowestPlayerId = 0;
        state.SettlementContributionTopValue = 0;
        state.SettlementContributionLowestValue = 0;
        state.SettlementContributionDecisiveTargetPlayerId = 0;
        state.SettlementContributionNominationSuccess = false;
        state.SettlementContributionEliminatedPlayerId = 0;
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
        state.SettlementNominations.Clear();
        state.BotNominationsInjected = false;
        ClearSettlementContributionResult(state);
        StartChecklistRound(matchingId, state);

        Logger.LogInformation("Round advanced: MatchingId={MatchingId}, Round={Round}", matchingId, state.RoundNumber);
    }

    private void StartChecklistRound(long matchingId, RoundRuntimeState state, bool broadcast = true)
    {
        var playerIds = GetChecklistActivePlayerIds(matchingId);
        if (playerIds.Count == 0)
            return;

        _checklistManager.StartRound(matchingId, state.RoundNumber, playerIds,
            playerId => ResolveChecklistChainContext(matchingId, playerId));
        if (broadcast)
            BroadcastChecklistInfo(matchingId);
    }

    private void BroadcastChecklistInfo(long matchingId)
    {
        foreach (var session in _getSessionsByInstance(CurrentMapId, matchingId))
        {
            if (session.PlayerId.HasValue)
                session.SendChecklistInfo();
        }

        if (CurrentMapSubId == matchingId && PlayerId.HasValue)
            SendChecklistInfo();
    }

    private List<long> GetChecklistActivePlayerIds(long matchingId)
    {
        var playerIds = GetSettlementActivePlayerIds(matchingId);

        if (CurrentMapSubId == matchingId
            && PlayerId.HasValue
            && !IsEliminated
            && ManittoStatus != ManittoStatus.SPECTATING)
        {
            playerIds.Add(PlayerId.Value);
        }

        return playerIds
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    private ChecklistChainContext ResolveChecklistChainContext(long matchingId, long playerId)
    {
        var myLink = _manittoChainManager.GetLink(matchingId, playerId);
        bool targetAlive = myLink != null && IsAliveChainPlayer(matchingId, myLink.TargetPlayerId);
        var manittoLink = _manittoChainManager.FindManittoOf(matchingId, playerId);
        bool manittoAlive = IsAliveChainLink(manittoLink);
        return new ChecklistChainContext(targetAlive, manittoAlive);
    }

    private bool IsAliveChainPlayer(long matchingId, long playerId)
    {
        var link = _manittoChainManager.GetLink(matchingId, playerId);
        return IsAliveChainLink(link);
    }

    private static bool IsAliveChainLink(ChainLink? link)
    {
        return link != null
               && link.Status != ManittoStatus.ELIMINATED
               && link.Status != ManittoStatus.SPECTATING;
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

    private void ApplySettlementElimination(long matchingId, RoundRuntimeState state)
    {
        if (state.SettlementEliminationApplied)
            return;

        long eliminatedPlayerId = state.SettlementContributionEliminatedPlayerId;
        if (eliminatedPlayerId == 0)
            eliminatedPlayerId = SelectRoundEliminationCandidate(matchingId) ?? 0;

        if (eliminatedPlayerId == 0 || !IsAliveChainPlayer(matchingId, eliminatedPlayerId))
        {
            state.SettlementEliminationApplied = true;
            Logger.LogWarning(
                "Settlement elimination skipped: MatchingId={MatchingId}, Round={Round}, EliminatedCandidate={Candidate}",
                matchingId, state.RoundNumber, eliminatedPlayerId);
            return;
        }

        state.SettlementContributionEliminatedPlayerId = eliminatedPlayerId;
        state.SettlementEliminationApplied = true;
        var reason = state.SettlementContributionNominationSuccess
            ? EliminationReason.DETECTED
            : EliminationReason.SETTLEMENT_LOW_CONTRIBUTION;

        Logger.LogInformation(
            "Settlement elimination applied: MatchingId={MatchingId}, Round={Round}, PlayerId={PlayerId}, Reason={Reason}",
            matchingId, state.RoundNumber, eliminatedPlayerId, reason);
        ProcessRoundElimination(matchingId, eliminatedPlayerId, reason);
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

    private void BroadcastPresenceNotebookUpdates(long matchingId, int roundNumber)
    {
        if (_presenceTracker == null) return;

        var sessions = _getSessionsByInstance(CurrentMapId, matchingId);
        if (sessions.Count == 0) return;

        var roster = GetSettlementActivePlayerIds(matchingId);
        if (roster.Count == 0) return;

        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue)
                continue;

            var records = _presenceTracker.GetNotebookRecords(
                matchingId,
                session.PlayerId.Value,
                roster,
                includeEmpty: true);
            session.SendPresenceNotebookUpdate(matchingId, roundNumber, records);
        }
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

    private void CleanupRoundTimer(long matchingId)
    {
        GameRoundStates.TryRemove(matchingId, out _);
        _checklistManager.RemoveMatchingState(matchingId);
        _presenceTracker?.Remove(matchingId);
        if (GameTimers.TryRemove(matchingId, out var timer))
            timer.Dispose();
    }

    private void EndGameByTimeout(long matchingId)
    {
        if (DevFlags.DisableGameEnd)
        {
            Logger.LogWarning("[DEV] 게임 종료 차단됨 (DISABLE_GAME_END=1): EndGameByTimeout matchingId={MatchingId}",
                matchingId);
            CleanupRoundTimer(matchingId);
            return;
        }

        Logger.LogInformation("게임 시간 초과: MatchingId={MatchingId}", matchingId);

        // 타이머 정리
        if (GameTimers.TryRemove(matchingId, out var timer))
            timer.Dispose();
        _checklistManager.RemoveMatchingState(matchingId);
        _presenceTracker?.Remove(matchingId);

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
