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

namespace game_server.sessions;

/// <summary>
///     탈락 상태와 알림을 처리하고, 매치 종료 확정·결과표 생성·결과 발행은 MatchResultService에 맡긴다.
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
        var eliminatedResultPlayers = _matchResults.BuildGameResultPlayers(allSessions, MatchingId, 0);

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
            _matchResults.SendGameResult(CurrentMapId, winnerId ?? 0, false, MatchingId);
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
        _matchResults.SendGameResult(CurrentMapId, winnerId, false, MatchingId, endReason, criterion);
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
