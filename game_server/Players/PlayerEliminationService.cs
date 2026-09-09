using game_server.matches;
using game_server.matches.results;
using game_server.items;
using game_server.logging;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.players;

/// <summary>
///     매치 잠금 안에서 탈락을 확정하고 인벤토리 드롭·탈락 알림·승자 판정을 순서대로 처리한다.
///     세션의 관전 상태는 전용 메서드로 반영하며, 결과 발행은 MatchResultService에 맡긴다.
///     호출자는 해당 매치 잠금을 소유해야 한다.
/// </summary>
internal sealed class PlayerEliminationService(
    MatchRuntimeStore _matchRuntimes,
    GameEventLogManager _gameEventLogManager,
    MatchResultService _matchResults,
    GroundItemDropService groundItemDrop,
    ILogger Logger)
{
    /// <summary>
    ///     플레이어 탈락 처리 + 탈락 브로드캐스트
    /// </summary>
    public void EliminatePlayer(long matchingId, long eliminatedPlayerId, EliminationReason reason, long? causePlayerId = null,
        bool deferGameOver = false, long attackerPlayerId = 0, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false, int forcedRank = 0)
    {
        var allSessions = _matchRuntimes.GetOrThrow(matchingId).Sessions.Values.ToList();
        var eliminatedSession = allSessions.FirstOrDefault(session => session.PlayerId == eliminatedPlayerId);
        var eliminatedBot = _matchRuntimes.GetOrThrow(matchingId).Bots.GetBot(matchingId, eliminatedPlayerId);
        AreaType eliminatedArea = eliminatedSession?.CurrentArea ?? eliminatedBot?.CurrentArea ?? AreaType.None;
        long resolvedAttackerPlayerId = attackerPlayerId != 0 ? attackerPlayerId : causePlayerId ?? 0;

        int finalOrbTier = _matchRuntimes.GetOrThrow(matchingId).Inventory.GetHighestOrbTier(eliminatedPlayerId);
        bool eliminated = _matchRuntimes.GetOrThrow(matchingId).Roster.TryEliminatePlayer(eliminatedPlayerId, reason,
            resolvedAttackerPlayerId, eliminatedArea, isAreaClosureElimination, isOvertimeElimination, forcedRank,
            finalOrbTier);
        if (!eliminated)
        {
            Logger.LogDebug(
                "Duplicate elimination ignored: matchingId={MatchingId}, PlayerId={PlayerId}, Reason={Reason}",
                matchingId, eliminatedPlayerId, reason);
            return;
        }

        // Keep the live session state authoritative as soon as elimination is accepted.
        if (eliminatedSession != null)
            eliminatedSession.ApplyMatchStatus(PlayerMatchStatus.ELIMINATED);
        if (eliminatedBot != null)
        {
            eliminatedBot.PlayerMatchStatus = PlayerMatchStatus.SPECTATING;
            eliminatedBot.IsEliminated = true;
        }

        _matchRuntimes.GetOrThrow(matchingId).GroundItems.ReleaseClaimReservationsForPlayer(eliminatedPlayerId);
        _gameEventLogManager.LogElimination(
            matchingId,
            eliminatedPlayerId,
            reason.ToString(),
            isBot: eliminatedBot != null,
            attackerPlayerId: resolvedAttackerPlayerId,
            isAreaClosureElimination: isAreaClosureElimination,
            isOvertimeElimination: isOvertimeElimination);

        if (eliminatedSession != null)
            groundItemDrop.DropAll(eliminatedSession);
        else
            DropBotInventoryAtCurrentPosition(matchingId, eliminatedPlayerId);

        // 1. 전체에게 탈락 알림. 탈락자에게만 결과표를 함께 보낸다.
        var eliminatedResultPlayers = _matchResults.BuildPlayerResults(allSessions, matchingId, 0);

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

        // Elimination removes the actor from the live world immediately. The eliminated session
        // remains connected for the result screen, so a dedicated leave packet is required.
        using (var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(eliminatedPlayerId))
        {
            foreach (var session in allSessions)
                session.TrySend(leavePacket);
        }

        // 3. 게임 종료 판정
        var (isGameOver, winnerId) = _matchRuntimes.GetOrThrow(matchingId).Roster.CheckGameOver();
        if (!deferGameOver && isGameOver)
        {
            Logger.LogInformation("게임 종료! 최후의 1인: {WinnerId}", winnerId);
            _matchResults.FinalizeMatch(matchingId, winnerId ?? 0);
        }

    }

    private void DropBotInventoryAtCurrentPosition(long matchingId, long botPlayerId)
    {
        var runtime = _matchRuntimes.GetOrThrow(matchingId);
        var outcome = EliminationInventoryDropper.DropBotInventoryWithLogs(
            runtime.Bots, runtime.Inventory, runtime.GroundItems, _gameEventLogManager,
            matchingId, botPlayerId);
        if (outcome == null || outcome.Drop.SpawnedItems.Count == 0)
            return;

        var targets = _matchRuntimes.GetOrThrow(matchingId).Sessions.Values.ToList()
            .Where(session => !session.IsEliminated && session.CurrentArea == outcome.Bot.CurrentArea);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN(
            (int)outcome.Bot.CurrentArea, outcome.Drop.SpawnedItems.ToList());
        foreach (var session in targets)
            session.TrySend(packet);
    }
}
