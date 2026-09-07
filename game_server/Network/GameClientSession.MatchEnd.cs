using System;
using System.Collections.Generic;
using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

/// <summary>
///     매치 종료 파이프라인: 탈락 처리(ProcessElimination) → 생존자 승리 판정(TryEndMatch) → 결과 전송(SendGameResult)·요약 영속·Redis 정리, 로스터 상태 브로드캐스트.
/// </summary>
public partial class GameClientSession
{
    /// <summary>
    ///     플레이어 탈락 처리 + 탈락 브로드캐스트
    /// </summary>
    private void ProcessElimination(long eliminatedPlayerId, EliminationReason reason, long? causePlayerId = null,
        bool deferGameOver = false, long attackerPlayerId = 0, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false, int forcedRank = 0)
    {
        var allSessions = _getSessionsByInstance(CurrentMapId, MatchingId);
        var eliminatedSession = allSessions.FirstOrDefault(session => session.PlayerId == eliminatedPlayerId);
        var eliminatedBot = _matchRuntimes.GetRequired(MatchingId).Bots.GetBot(MatchingId, eliminatedPlayerId);
        AreaType eliminatedArea = eliminatedSession?.CurrentArea ?? eliminatedBot?.CurrentArea ?? AreaType.None;
        long resolvedAttackerPlayerId = attackerPlayerId != 0 ? attackerPlayerId : causePlayerId ?? 0;

        int finalOrbTier = _matchRuntimes.GetRequired(MatchingId).Inventory.GetEquippedBattleItemTier(eliminatedPlayerId);
        var transition = _matchRuntimes.GetRequired(MatchingId).Roster.TryEliminatePlayer(eliminatedPlayerId, reason,
            resolvedAttackerPlayerId, eliminatedArea, isAreaClosureElimination, isOvertimeElimination, forcedRank,
            finalOrbTier);
        if (!transition.Applied)
        {
            Logger.LogDebug(
                "Duplicate elimination ignored: MatchingId={MatchingId}, PlayerId={PlayerId}, Reason={Reason}",
                MatchingId, eliminatedPlayerId, reason);
            return;
        }

        // Keep the live session state authoritative as soon as elimination is accepted.
        if (eliminatedSession != null)
            eliminatedSession.PlayerMatchStatus = PlayerMatchStatus.SPECTATING;
        if (eliminatedBot != null)
        {
            eliminatedBot.PlayerMatchStatus = PlayerMatchStatus.SPECTATING;
            eliminatedBot.IsEliminated = true;
        }

        var affected = transition.AffectedPlayers;
        _matchRuntimes.GetRequired(MatchingId).GroundItems.ReleaseClaimReservationsForPlayer(eliminatedPlayerId);
        _gameEventLogManager.LogElimination(
            MatchingId,
            eliminatedPlayerId,
            reason.ToString(),
            isBot: eliminatedBot != null,
            attackerPlayerId: resolvedAttackerPlayerId,
            isAreaClosureElimination: isAreaClosureElimination,
            isOvertimeElimination: isOvertimeElimination);

        if (eliminatedSession != null)
            eliminatedSession.DropAllInventoryAtCurrentPosition();
        else
            DropBotInventoryAtCurrentPosition(eliminatedPlayerId);

        // 1. 전체에게 탈락 알림. 탈락자에게만 결과표를 함께 보낸다.
        var eliminatedResultPlayers = BuildGameResultPlayers(allSessions, MatchingId, 0);

        foreach (var session in allSessions)
        {
            using var eliminatedPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED);
            var eliminatedMsg = new G_TO_C_PLAYER_ELIMINATED
            {
                PlayerId = eliminatedPlayerId,
                AttackerPlayerId = resolvedAttackerPlayerId,
                Reason = reason,
                ResultPlayers = session.PlayerId == eliminatedPlayerId ? eliminatedResultPlayers : []
            };
            eliminatedPacket.SetBody(MessagePackSerializer.Serialize(eliminatedMsg));
            session.TrySend(eliminatedPacket);
        }

        // 세션 PlayerMatchStatus 동기화 (탈락자 → SPECTATING으로 관전 전환)
        // #26: 봇 상태도 함께 동기화 (BotPlayerManager)
        foreach (var (playerId, newStatus) in affected)
        {
            var s = allSessions.FirstOrDefault(s => s.PlayerId == playerId);
            if (s != null)
            {
                s.PlayerMatchStatus = newStatus == PlayerMatchStatus.ELIMINATED
                    ? PlayerMatchStatus.SPECTATING
                    : newStatus;
                continue;
            }

            // 봇 상태 동기화
            var bot = _matchRuntimes.GetRequired(MatchingId).Bots.GetBot(MatchingId, playerId);
            if (bot != null)
            {
                if (newStatus == PlayerMatchStatus.ELIMINATED)
                {
                    bot.IsEliminated = true;
                    bot.PlayerMatchStatus = PlayerMatchStatus.SPECTATING;
                }
                else
                {
                    bot.PlayerMatchStatus = newStatus;
                }
            }
        }

        // Elimination removes the actor from the live world immediately. The eliminated session
        // remains connected for the result screen, so a dedicated leave packet is required.
        using (var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(eliminatedPlayerId))
        {
            foreach (var session in allSessions)
                session.TrySend(leavePacket);
        }

        // 3. 게임 종료 판정
        var (isGameOver, winnerId) = _matchRuntimes.GetRequired(MatchingId).Roster.CheckGameOver();
        if (!deferGameOver && isGameOver)
        {
            Logger.LogInformation("게임 종료! 최후의 1인: {WinnerId}", winnerId);
            SendGameResult(winnerId ?? 0, false, MatchingId);
        }

    }

    internal void EliminateForSettlement(
        long eliminatedPlayerId,
        bool isAreaClosureElimination,
        bool isOvertimeElimination,
        int forcedRank)
    {
        ProcessElimination(
            eliminatedPlayerId,
            EliminationReason.MENTAL_ZERO,
            deferGameOver: true,
            isAreaClosureElimination: isAreaClosureElimination,
            isOvertimeElimination: isOvertimeElimination,
            forcedRank: forcedRank);
    }

    internal void TryEndMatch(long winnerId, string criterion)
    {
        if (_devOptions.DisableGameEnd)
        {
            Logger.LogWarning(
                "[DEV] 게임 종료 차단됨 (DISABLE_GAME_END=1): TryEndMatch winner={WinnerId}, criterion={Criterion}",
                winnerId, criterion);
            return;
        }
        if (Volatile.Read(ref _isGameEnded) || MatchingId <= 0)
            return;

        var allSessions = _getSessionsByInstance(CurrentMapId, MatchingId);
        Logger.LogInformation(
            "Swarm match resolved: MatchingId={MatchingId}, WinnerId={WinnerId}, Criterion={Criterion}",
            MatchingId, winnerId, criterion);
        _gameEventLogManager.LogSystem(
            MatchingId,
            $"survivor_settlement winner={winnerId} criterion={criterion}");
        // 오브 점수 만료(#226 단계 B)는 요약 EndReason에도 그대로 남긴다 — 계측에서
        // 연장전 정산과 섞이면 5분 판정 발화율을 셀 수 없다.
        string endReason = criterion == "orb_score_timeout" ? criterion : "overtime_settlement";
        SendGameResult(winnerId, false, MatchingId, endReason, criterion);
    }

    /// <summary>
    ///     매치 종료 결과를 매치 잠금 안에서 확정·전송한다: 터미널 표시 → 결과표 동결 → 이벤트 로그·요약 캡처 →
    ///     GAME_RESULT → 세션별 GAME_END → MarkGameEnded. 마지막 전투 패킷이 이미 같은 잠금 안에서
    ///     나갔으므로 결과는 반드시 그 뒤에 온다. lifecycle 발행과 요약 파일 쓰기는 잠금이 풀린 뒤 후처리로 돈다.
    /// </summary>
    private void SendGameResult(long winnerId, bool isTimeout, long matchingId,
        string endReason = "last_survivor", string tieBreakCriterion = "not_required")
    {
        if (_devOptions.DisableGameEnd)
        {
            Logger.LogWarning(
                "[DEV] 게임 종료 결과 전송 차단됨 (DISABLE_GAME_END=1): MatchingId={MatchingId}, reason={Reason}",
                matchingId, endReason);
            return;
        }

        MatchRuntime? runtime = _matchRuntimes.Get(matchingId);
        if (runtime == null)
        {
            Logger.LogDebug("Terminal match result preparation rejected: MatchingId={MatchingId}", matchingId);
            return;
        }

        using MatchScope scope = _matchRuntimes.Enter(runtime);
        if (!runtime.TryMarkTerminal())
        {
            Logger.LogDebug("Duplicate match finalization ignored: MatchingId={MatchingId}", matchingId);
            return;
        }

        List<GameClientSession> sessionSnapshot = _getSessionsByInstance(CurrentMapId, matchingId);
        var players = BuildGameResultPlayers(sessionSnapshot, matchingId, winnerId);
        byte[] resultPayload = MessagePackSerializer.Serialize(new G_TO_C_GAME_RESULT
        {
            WinnerId = winnerId,
            IsTimeout = isTimeout,
            Players = players
        });
        MatchTerminalSessionPublication[] sessionPublications = sessionSnapshot
            .Select(session => new MatchTerminalSessionPublication(
                session,
                MessagePackSerializer.Serialize(new G_TO_C_GAME_END
                {
                    MatchingId = matchingId,
                    IsEscaped = !isTimeout && session.PlayerId == winnerId
                })))
            .ToArray();
        var terminalPlan = new MatchTerminalPublicationPlan(
            matchingId,
            resultPayload,
            sessionPublications);

        MatchSummaryPersistenceRequest? summaryRequest = null;
        if (_gameEventLogManager.TryBeginFinalization(matchingId))
        {
            try
            {
                _gameEventLogManager.LogMatchEnded(
                    matchingId,
                    winnerId,
                    endReason,
                    tieBreakCriterion,
                    players.Select(player => new MatchFinalPlayerStats(
                        player.PlayerId,
                        player.Rank,
                        player.SurvivalTimeSeconds,
                        player.KillCount,
                        player.TotalDamageDealt,
                        player.TotalRecovery,
                        player.OrbCount)).ToList());
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Final match event logging failed; summary capture will continue: MatchingId={MatchingId}",
                    matchingId);
            }

            try
            {
                summaryRequest = MatchSummaryPersistence.Capture(
                    _gameEventLogManager,
                    Logger,
                    matchingId,
                    endReason,
                    winnerId);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Final match summary capture failed; terminal publication will continue: MatchingId={MatchingId}",
                    matchingId);
            }
        }

        // 결과·종료 패킷은 잠금 안에서 — 계측·요약 캡처가 실패해도 클라이언트는 반드시 결과를 받는다.
        PublishTerminalResult(terminalPlan);
        MatchSummaryPersistenceRequest? capturedSummary = summaryRequest;
        runtime.AfterRelease.Add(() =>
        {
            DispatchMatchingLifecyclePublications(
                matchingId,
                terminalPlan.LifecyclePublications);
            if (capturedSummary != null)
            {
                MatchSummaryPersistence.Persist(
                    capturedSummary,
                    _matchSummaryFileStore,
                    Logger);
            }
        });
    }

    private void PublishTerminalResult(MatchTerminalPublicationPlan plan)
    {
        foreach (MatchTerminalSessionPublication publication in plan.SessionPublications)
        {
            RunTerminalPublicationStep(
                plan.MatchingId,
                publication.Session,
                "GAME_RESULT",
                () =>
                {
                    using var resultPacket = Packet.Create((int)Protocol.G_TO_C_GAME_RESULT);
                    resultPacket.SetBody(plan.GameResultPayload);
                    publication.Session.TrySend(resultPacket);
                });
        }

        foreach (MatchTerminalSessionPublication publication in plan.SessionPublications)
        {
            RunTerminalPublicationStep(
                plan.MatchingId,
                publication.Session,
                "GAME_END",
                () =>
                {
                    using var endPacket = Packet.Create((int)Protocol.G_TO_C_GAME_END);
                    endPacket.SetBody(publication.GameEndPayload);
                    publication.Session.TrySend(endPacket);
                });
        }

        foreach (MatchTerminalSessionPublication publication in plan.SessionPublications)
        {
            Action? lifecyclePublication = null;
            RunTerminalPublicationStep(
                plan.MatchingId,
                publication.Session,
                "MarkGameEnded",
                () => lifecyclePublication =
                    publication.Session.MarkGameEndedAndPrepareLifecyclePublication());
            if (lifecyclePublication != null)
                plan.LifecyclePublications.Add(lifecyclePublication);
        }
    }

    private void DispatchMatchingLifecyclePublications(
        long matchingId,
        IReadOnlyList<Action> lifecyclePublications)
    {
        foreach (Action dispatch in lifecyclePublications)
        {
            try
            {
                dispatch();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Deferred matching lifecycle dispatch failed: MatchingId={MatchingId}",
                    matchingId);
            }
        }
    }

    private void RunTerminalPublicationStep(
        long matchingId,
        GameClientSession session,
        string component,
        Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Match terminal publication failed: MatchingId={MatchingId}, PlayerId={PlayerId}, Component={Component}",
                matchingId,
                session.PlayerId,
                component);
        }
    }

    private sealed record MatchTerminalPublicationPlan(
        long MatchingId,
        byte[] GameResultPayload,
        MatchTerminalSessionPublication[] SessionPublications)
    {
        public List<Action> LifecyclePublications { get; } = [];
    }

    private sealed record MatchTerminalSessionPublication(
        GameClientSession Session,
        byte[] GameEndPayload);

    private List<GameResultPlayerInfo> BuildGameResultPlayers(List<GameClientSession> allSessions, long matchingId,
        long winnerId)
    {
        DateTime endedAtUtc = DateTime.UtcNow;
        DateTime startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId) ?? endedAtUtc;
        var resultRows = _matchRuntimes.GetRequired(matchingId).Roster.BuildGameResult();
        var killCountsByPlayerId = resultRows
            .Where(row => row.attackerPlayerId != 0 && row.reason == EliminationReason.MENTAL_ZERO)
            .GroupBy(row => row.attackerPlayerId)
            .ToDictionary(group => group.Key, group => group.Count());

        var rows = resultRows
            .Select(d =>
            {
                var session = allSessions.FirstOrDefault(s => s.PlayerId == d.playerId);
                var bot = _matchRuntimes.GetRequired(matchingId).Bots.GetBot(matchingId, d.playerId);
                var playerInfo = bot == null ? null : _matchRuntimes.GetRequired(matchingId).Bots.SynthesizePlayerInfo(matchingId, d.playerId);
                var playerProfile = _matchRuntimes.GetRequired(matchingId).Roster.GetPlayerProfile(d.playerId);
                var stats = _gameEventLogManager.GetResultStats(matchingId, d.playerId);
                var orbScore = ResolveResultOrbScore(matchingId, d.playerId);
                DateTime survivalEndUtc = d.eliminatedAt ?? endedAtUtc;
                int survivalSeconds = Math.Max(0, (int)Math.Floor((survivalEndUtc - startedAtUtc).TotalSeconds));

                return new
                {
                    Info = new GameResultPlayerInfo
                    {
                        PlayerId = d.playerId,
                        Name = ResolveResultPlayerName(d.playerId, playerInfo, playerProfile, bot),
                        EliminationReason = d.reason,
                        FinalStatus = d.finalStatus,
                        Corruption = session?.Corruption ?? bot?.Corruption ?? 0,
                        MaxCorruption = MaxCorruption,
                        WearItemIdList = playerProfile?.WearItemIdList is { Count: > 0 }
                            ? new List<int>(playerProfile.WearItemIdList)
                            : playerInfo?.WearItemIdList != null
                            ? new List<int>(playerInfo.WearItemIdList)
                            : new List<int>(),
                        SurvivalTimeSeconds = survivalSeconds,
                        // 실제 탈락 결과를 기준으로 집계해 전투 로그 누락/중복과 무관하게 결과표를 맞춘다.
                        // 스웜은 여기에 몹 처치를 더한다 (#229) — 플레이어가 죽인 건 거의 전부 몹이다.
                        KillCount = (killCountsByPlayerId.TryGetValue(d.playerId, out int killCount) ? killCount : 0)
                                    + stats.MonsterKillCount,
                        TotalDamageDealt = stats.TotalDamageDealt + stats.MonsterDamageDealt,
                        TotalRecovery = stats.TotalRecovery,
                        AttackerPlayerId = d.attackerPlayerId,
                        EliminatedArea = d.eliminatedArea,
                        IsAreaClosureElimination = d.isAreaClosureElimination,
                        IsOvertimeElimination = d.isOvertimeElimination,
                        Rank = d.playerId == winnerId ? 1 : d.eliminationRank,
                        FinalOrbTier = d.playerId == winnerId ? _matchRuntimes.GetRequired(matchingId).Inventory.GetEquippedBattleItemTier(d.playerId) : d.finalOrbTier,
                        // 결과 승점은 오브 수 (#229): 인게임 순위와 같은 눈금을 쓴다.
                        OrbCount = orbScore.OrbCount
                    },
                    EliminatedAt = d.eliminatedAt
                };
            })
            .ToList();

        return GameResultRankingResolver.Resolve(rows.Select(row => row.Info), winnerId);
    }

    /// <summary>
    ///     결과 화면 승점 (#229). 인게임 오브 순위(GetSwarmOrbScore)와 같은 계산이다 —
    ///     한쪽만 바뀌면 5분 내내 보던 순위와 결과표가 어긋난다.
    ///     탈락자는 인벤토리가 비어 자연히 0이 된다.
    /// </summary>
    private (int OrbCount, int TierSum) ResolveResultOrbScore(long matchingId, long playerId)
    {
        var inventory = _matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId);
        int orbCount = 0;
        int tierSum = 0;
        foreach (var item in inventory.GetAllItems())
        {
            if (item.Count <= 0) continue;
            if (!OrbData.TryGetColorAndTier(item.ItemId, out _, out int tier) &&
                !OrbData.TryGetRecoveryTier(item.ItemId, out tier))
                continue;
            if (tier <= 0) continue;

            // 레거시 스택(같은 색·티어가 한 항목) 호환 — 항목이 아니라 수량이 오브 수다.
            orbCount += item.Count;
            tierSum += tier * item.Count;
        }

        return (orbCount, tierSum);
    }

    private static string ResolveResultPlayerName(
        long playerId,
        PlayerInfo? playerInfo,
        MatchPlayerProfile? playerProfile,
        BotPlayerState? bot)
    {
        if (!string.IsNullOrEmpty(playerProfile?.Name)) return playerProfile.Name;
        if (!string.IsNullOrEmpty(playerInfo?.Name)) return playerInfo.Name;
        if (!string.IsNullOrEmpty(bot?.Name)) return bot.Name;
        return BotPlayerManager.IsBotPlayerId(playerId) ? $"Player{Math.Abs(playerId)}" : $"Player{playerId}";
    }

    /// <summary>
    ///     정신력 100 도달 시 탈락 체크. 권고안 B(2026-05-05): Stamina 0 단독으로는 탈락 트리거 안 됨
    ///     (대신 ModifyStats가 Stamina 부족분을 Corruption 1:2 변환).
    /// </summary>
    public void CheckResourceElimination(long attackerPlayerId = 0, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false)
    {
        if (!PlayerId.HasValue || Volatile.Read(ref _isGameEnded) || IsEliminated) return;
        if (Corruption < MaxCorruption) return;

        Logger.LogInformation(
            "[Resource] Mental depleted: PlayerId={PlayerId}, Corruption={Corruption}/{MaxCorruption}. Eliminating player.",
            PlayerId.Value, Corruption, MaxCorruption);
        ProcessElimination(PlayerId.Value, EliminationReason.MENTAL_ZERO,
            attackerPlayerId: attackerPlayerId, isAreaClosureElimination: isAreaClosureElimination,
            isOvertimeElimination: isOvertimeElimination);
    }
}
