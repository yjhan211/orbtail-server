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
    ///     플레이어 탈락 처리 + 체인 단절 브로드캐스트
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

        int finalOrbTier = ResolveFinalOrbTier(CurrentMapSubId, eliminatedPlayerId);
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
        // #26: 봇 상태도 함께 동기화 (BotPlayerManager) — 시한부 진입 시 사보타주 트리거 등
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
            SendGameResult(allSessions, winnerId ?? 0, false, CurrentMapSubId);
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
        SendGameResult(allSessions, winnerId, false, CurrentMapSubId, endReason, criterion);
    }

    private int ResolveFinalOrbTier(long matchingId, long playerId)
    {
        var equippedItem = _inGameInventoryManager.GetEquippedBattleItem(matchingId, playerId);
        return equippedItem == null ? 0 : BattleItemCombatData.Get(equippedItem.ItemId)?.Tier ?? 0;
    }

    /// <summary>
    ///     게임 결과 패킷 전송 (체인 전체 공개)
    /// </summary>
    private void SendGameResult(List<GameClientSession> allSessions, long winnerId, bool isTimeout, long matchingId,
        string endReason = "last_survivor", string tieBreakCriterion = "not_required")
    {
        if (DevFlags.DisableGameEnd)
        {
            Logger.LogWarning(
                "[DEV] 게임 종료 결과 전송 차단됨 (DISABLE_GAME_END=1): MatchingId={MatchingId}, reason={Reason}",
                matchingId, endReason);
            return;
        }
        if (allSessions.Any(session => session.IsGameEnded) ||
            !_gameEventLogManager.TryBeginFinalization(matchingId))
        {
            Logger.LogDebug("Duplicate match finalization ignored: MatchingId={MatchingId}", matchingId);
            return;
        }

        var players = BuildGameResultPlayers(allSessions, matchingId, winnerId);
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
        PersistMatchSummary(matchingId, endReason, winnerId);

        var resultChunks = GameResultPacketChunker.CreateGameResultChunks(winnerId, isTimeout, players);
        foreach (var resultChunk in resultChunks)
        {
            using var resultPacket = Packet.Create((int)Protocol.G_TO_C_GAME_RESULT);
            resultPacket.SetBody(MessagePackSerializer.Serialize(resultChunk));
            foreach (var session in allSessions) session.Send(resultPacket);
        }

        // 기존 게임 종료 패킷도 전송 (클라이언트 호환)
        foreach (var session in allSessions)
        {
            bool isEscaped = !isTimeout && session.PlayerId == winnerId;
            using var endPacket = PacketMaker.G_TO_C_GAME_END(matchingId, isEscaped);
            session.Send(endPacket);
        }

        // 결과 화면 이후 퇴장은 페널티 면제
        foreach (var session in allSessions) session.MarkGameEnded();

        _cleanupMatchRuntime(matchingId);
    }

    private void PersistMatchSummary(long matchingId, string endReason, long winnerId)
    {
        try
        {
            var events = _gameEventLogManager.GetForPersistence(matchingId);
            var summary = _matchSummaryFileStore.Save(matchingId, endReason, winnerId, events);
            Logger.LogInformation(
                "Match summary persisted: MatchingId={MatchingId}, EndReason={EndReason}, Events={EventCount}, Directory={Directory}",
                matchingId, summary.EndReason, summary.RawEventCount, _matchSummaryFileStore.DirectoryPath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to persist match summary: MatchingId={MatchingId}", matchingId);
        }
    }

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
                        JobTitle = d.job,
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
                        FinalOrbTier = d.playerId == winnerId ? ResolveFinalOrbTier(matchingId, d.playerId) : d.finalOrbTier,
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
