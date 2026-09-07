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
        if (Volatile.Read(ref _isGameEnded) || MatchingId <= 0)
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
        if (!PlayerId.HasValue || Volatile.Read(ref _isGameEnded) || IsEliminated) return;
        if (Health > 0) return;

        Logger.LogInformation(
            "[Resource] Health depleted: PlayerId={PlayerId}, Health={Health}/{MaxHealth}. Eliminating player.",
            PlayerId.Value, Health, Config.MAX_HEALTH);
        _matchEliminations.Process(CurrentMapId, MatchingId, PlayerId.Value, EliminationReason.HEALTH_ZERO,
            attackerPlayerId: attackerPlayerId, isAreaClosureElimination: isAreaClosureElimination,
            isOvertimeElimination: isOvertimeElimination);
    }
}
