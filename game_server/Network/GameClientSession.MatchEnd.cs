using System;
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
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var eliminatedSession = allSessions.FirstOrDefault(session => session.PlayerId == eliminatedPlayerId);
        var eliminatedBot = _botPlayerManager.GetBot(CurrentMapSubId, eliminatedPlayerId);
        AreaType eliminatedArea = eliminatedSession?.CurrentArea ?? eliminatedBot?.CurrentArea ?? AreaType.None;
        long resolvedAttackerPlayerId = attackerPlayerId != 0 ? attackerPlayerId : causePlayerId ?? 0;

        int finalOrbTier = _inGameInventoryManager.GetEquippedBattleItemTier(CurrentMapSubId, eliminatedPlayerId);
        var transition = _matchRosterManager.TryEliminatePlayer(CurrentMapSubId, eliminatedPlayerId, reason,
            resolvedAttackerPlayerId, eliminatedArea, isAreaClosureElimination, isOvertimeElimination, forcedRank,
            finalOrbTier);
        if (!transition.Applied)
        {
            Logger.LogDebug(
                "Duplicate elimination ignored: MatchingId={MatchingId}, PlayerId={PlayerId}, Reason={Reason}",
                CurrentMapSubId, eliminatedPlayerId, reason);
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
        _groundItemManager.ReleaseClaimReservationsForPlayer(CurrentMapSubId, eliminatedPlayerId);
        _gameEventLogManager.LogElimination(
            CurrentMapSubId,
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

        // 1. 전체에게 탈락 알림. 탈락자에게만 결과표를 고정 패킷 예산 안에서 나눠 보낸다.
        var eliminatedResultPlayers = BuildGameResultPlayers(allSessions, CurrentMapSubId, 0);
        var eliminatedResultChunks = GameResultPacketChunker.CreateEliminationChunks(
            eliminatedPlayerId,
            resolvedAttackerPlayerId,
            reason,
            eliminatedResultPlayers);

        foreach (var session in allSessions)
        {
            if (session.PlayerId == eliminatedPlayerId)
            {
                foreach (var resultChunk in eliminatedResultChunks)
                {
                    using var resultPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED);
                    resultPacket.SetBody(MessagePackSerializer.Serialize(resultChunk));
                    session.Send(resultPacket);
                }

                continue;
            }

            using var eliminatedPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED);
            var eliminatedMsg = new G_TO_C_PLAYER_ELIMINATED
            {
                PlayerId = eliminatedPlayerId,
                AttackerPlayerId = resolvedAttackerPlayerId,
                Reason = reason,
                ResultPlayers = [],
                ResultChunkIndex = 0,
                IsResultEnd = true
            };
            eliminatedPacket.SetBody(MessagePackSerializer.Serialize(eliminatedMsg));
            session.Send(eliminatedPacket);
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
            var bot = _botPlayerManager.GetBot(CurrentMapSubId, playerId);
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
                session.Send(leavePacket);
        }

        // 3. 게임 종료 판정
        var (isGameOver, winnerId) = _matchRosterManager.CheckGameOver(CurrentMapSubId);
        if (!deferGameOver && isGameOver)
        {
            Logger.LogInformation("게임 종료! 최후의 1인: {WinnerId}", winnerId);
            SendGameResult(winnerId ?? 0, false, CurrentMapSubId);
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
        if (DevFlags.DisableGameEnd)
        {
            Logger.LogWarning(
                "[DEV] 게임 종료 차단됨 (DISABLE_GAME_END=1): TryEndMatch winner={WinnerId}, criterion={Criterion}",
                winnerId, criterion);
            return;
        }
        if (Volatile.Read(ref _isGameEnded) || CurrentMapSubId <= 0)
            return;

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        Logger.LogInformation(
            "Survivor match resolved: MatchingId={MatchingId}, WinnerId={WinnerId}, Criterion={Criterion}",
            CurrentMapSubId, winnerId, criterion);
        _gameEventLogManager.LogSystem(
            CurrentMapSubId,
            $"survivor_settlement winner={winnerId} criterion={criterion}");
        // 오브 점수 만료(#226 단계 B)는 요약 EndReason에도 그대로 남긴다 — 계측에서
        // 연장전 정산과 섞이면 5분 판정 발화율을 셀 수 없다.
        string endReason = criterion == "orb_score_timeout" ? criterion : "overtime_settlement";
        SendGameResult(winnerId, false, CurrentMapSubId, endReason, criterion);
    }

    /// <summary>
    ///     게임 결과 패킷 전송 (전체 로스터 공개)
    /// </summary>
    private void SendGameResult(long winnerId, bool isTimeout, long matchingId,
        string endReason = "last_survivor", string tieBreakCriterion = "not_required")
    {
        if (DevFlags.DisableGameEnd)
        {
            Logger.LogWarning(
                "[DEV] 게임 종료 결과 전송 차단됨 (DISABLE_GAME_END=1): MatchingId={MatchingId}, reason={Reason}",
                matchingId, endReason);
            return;
        }

        MatchTerminalPublicationPlan? terminalPlan = null;
        MatchSummaryPersistenceRequest? summaryRequest = null;
        using IDisposable? runtimeOperation = _acquireMatchRuntimeOperation(
            matchingId,
            () =>
            {
                List<GameClientSession> sessionSnapshot =
                    _getSessionsByInstance(CurrentMapId, matchingId);
                if (sessionSnapshot.Any(session => session.IsGameEnded))
                    return;

                var players = BuildGameResultPlayers(sessionSnapshot, matchingId, winnerId);
                byte[][] resultPayloads = GameResultPacketChunker
                    .CreateGameResultChunks(winnerId, isTimeout, players)
                    .Select(resultChunk => MessagePackSerializer.Serialize(resultChunk))
                    .ToArray();
                MatchTerminalSessionPublication[] sessionPublications = sessionSnapshot
                    .Select(session => new MatchTerminalSessionPublication(
                        session,
                        MessagePackSerializer.Serialize(new G_TO_C_GAME_END
                        {
                            MatchingId = matchingId,
                            IsEscaped = !isTimeout && session.PlayerId == winnerId
                        })))
                    .ToArray();
                var preparedTerminalPlan = new MatchTerminalPublicationPlan(
                    matchingId,
                    resultPayloads,
                    sessionPublications);

                if (!_gameEventLogManager.TryBeginFinalization(matchingId))
                    return;

                try
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
                finally
                {
                    // Once TryBeginFinalization succeeds, the captured terminal plan must always
                    // reach lifecycle cleanup even when observational event/summary capture fails.
                    // Register while the operation acquisition still owns SyncRoot so no gameplay
                    // action can mutate the already serialized result before Active -> Finalizing.
                    terminalPlan = preparedTerminalPlan;
                    Action? afterFinalized = null;
                    if (summaryRequest != null)
                    {
                        MatchSummaryPersistenceRequest capturedSummary = summaryRequest;
                        afterFinalized = () =>
                            MatchSummaryPersistence.Persist(capturedSummary, _matchSummaryFileStore, Logger);
                    }

                    _cleanupMatchRuntime(
                        matchingId,
                        () => PublishTerminalResult(preparedTerminalPlan),
                        afterFinalized);
                }
            });

        if (runtimeOperation == null)
        {
            Logger.LogDebug("Terminal match result preparation rejected: MatchingId={MatchingId}", matchingId);
            return;
        }

        if (terminalPlan == null)
        {
            Logger.LogDebug("Duplicate match finalization ignored: MatchingId={MatchingId}", matchingId);
            return;
        }
    }

    private void PublishTerminalResult(MatchTerminalPublicationPlan plan)
    {
        foreach (byte[] resultPayload in plan.GameResultPayloads)
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
                        resultPacket.SetBody(resultPayload);
                        publication.Session.Send(resultPacket);
                    });
            }
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
                    publication.Session.Send(endPacket);
                });
        }

        foreach (MatchTerminalSessionPublication publication in plan.SessionPublications)
        {
            RunTerminalPublicationStep(
                plan.MatchingId,
                publication.Session,
                "MarkGameEnded",
                publication.Session.MarkGameEnded);
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
        byte[][] GameResultPayloads,
        MatchTerminalSessionPublication[] SessionPublications);

    private sealed record MatchTerminalSessionPublication(
        GameClientSession Session,
        byte[] GameEndPayload);

    private List<GameResultPlayerInfo> BuildGameResultPlayers(List<GameClientSession> allSessions, long matchingId,
        long winnerId)
    {
        DateTime endedAtUtc = DateTime.UtcNow;
        DateTime startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId) ?? endedAtUtc;
        var resultRows = _matchRosterManager.BuildGameResult(matchingId);
        var killCountsByPlayerId = resultRows
            .Where(row => row.attackerPlayerId != 0 && row.reason == EliminationReason.MENTAL_ZERO)
            .GroupBy(row => row.attackerPlayerId)
            .ToDictionary(group => group.Key, group => group.Count());

        var rows = resultRows
            .Select(d =>
            {
                var session = allSessions.FirstOrDefault(s => s.PlayerId == d.playerId);
                var bot = _botPlayerManager.GetBot(matchingId, d.playerId);
                var playerInfo = bot == null ? null : _botPlayerManager.SynthesizePlayerInfo(matchingId, d.playerId);
                var playerProfile = _matchRosterManager.GetPlayerProfile(matchingId, d.playerId);
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
                        TargetPlayerId = d.targetId,
                        WatcherPlayerId = d.watcherId,
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
                        FinalOrbTier = d.playerId == winnerId ? _inGameInventoryManager.GetEquippedBattleItemTier(matchingId, d.playerId) : d.finalOrbTier,
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
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId);
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
