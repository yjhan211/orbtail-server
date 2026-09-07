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
///     세션의 탈락 조건을 확인하고 매치 탈락·종료 처리는 MatchEliminationService에 맡긴다.
/// </summary>
public partial class GameClientSession
{
    internal void EliminateForSettlement(
        long eliminatedPlayerId,
        bool isAreaClosureElimination,
        bool isOvertimeElimination,
        int forcedRank)
    {
        _matchEliminations.Process(CurrentMapId, MatchingId,
            eliminatedPlayerId,
            EliminationReason.HEALTH_ZERO,
            deferGameOver: true,
            isAreaClosureElimination: isAreaClosureElimination,
            isOvertimeElimination: isOvertimeElimination,
            forcedRank: forcedRank);
    }

    internal void TryEndMatch(long winnerId, string criterion)
    {
        if (IsGameEnded || MatchingId <= 0)
            return;
        _matchEliminations.EndMatch(CurrentMapId, MatchingId, winnerId, criterion);
    }

    /// <summary>로스터의 탈락 상태를 연결의 관전 상태로 반영한다.</summary>
    internal void ApplyMatchStatus(PlayerMatchStatus status)
    {
        PlayerMatchStatus = status == PlayerMatchStatus.ELIMINATED
            ? PlayerMatchStatus.SPECTATING
            : status;
    }
    /// <summary>
    ///     체력이 0이면 탈락한다. 스태미나가 0이라는 이유만으로는 탈락하지 않는다.
    ///     스태미나 부족에 따른 추가 체력 피해는 ModifyStats가 적용한다.
    /// </summary>
    public void CheckResourceElimination(long attackerPlayerId = 0, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false)
    {
        if (!PlayerId.HasValue || IsGameEnded || IsEliminated) return;
        if (Health > 0) return;

        Logger.LogInformation(
            "[Resource] Health depleted: PlayerId={PlayerId}, Health={Health}/{MaxHealth}. Eliminating player.",
            PlayerId.Value, Health, Config.MAX_HEALTH);
        _matchEliminations.Process(CurrentMapId, MatchingId, PlayerId.Value, EliminationReason.HEALTH_ZERO,
            attackerPlayerId: attackerPlayerId, isAreaClosureElimination: isAreaClosureElimination,
            isOvertimeElimination: isOvertimeElimination);
    }

    /// <summary>
    ///     `GAME_RESULT`와 `GAME_END` 시도 뒤 마지막 best-effort mark 단계에서 호출한다.
    ///     completed terminal을 선점해 이후 소켓 종료가 다른 종료 원인으로 덮어쓰지 못하게 한다.
    ///     이 public wrapper는 준비된 lifecycle Action을 즉시 dispatch한다.
    /// </summary>
    public void MarkGameEnded()
    {
        Action? dispatch = MarkGameEndedAndPrepareLifecyclePublication();
        dispatch?.Invoke();
    }

    /// <summary>
    ///     세션을 터미널로 표시하고 lifecycle Action을 준비만 한다(발행 안 함). 매치 종료 경로가
    ///     잠금 안에서 부르고, 발행은 잠금이 풀린 뒤 후처리에서 한다.
    /// </summary>
    internal Action? MarkGameEndedAndPrepareLifecyclePublication()
    {
        Volatile.Write(ref _isGameEnded, true);
        if (!PlayerId.HasValue || MatchingId <= 0 || !TryBeginMatchingLifecycleTerminal())
            return null;

        try
        {
            return _matchingLifecycle.PrepareGameCompletion(PlayerId.Value, MatchingId);
        }
        catch
        {
            Volatile.Write(ref _matchingLifecycleTerminalReported, 0);
            throw;
        }
    }
}
