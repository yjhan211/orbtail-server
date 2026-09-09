using game_server.items;
using game_server.logging;
using game_server.matches.results;
using game_server.matches;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.bots;

/// <summary>
///     봇 탈락에 따른 순위 기록, 아이템 드롭, 참가자 알림과 게임 종료 확인을 처리한다.
///     호출자는 해당 매치의 잠금을 잡은 상태로 호출한다. 매치 상태는 전달받은 MatchRuntime에만 기록한다.
/// </summary>
internal sealed class BotEliminationService(
    GameEventLogManager eventLogs,
    MatchEliminationService matchEliminations,
    ILogger logger)
{
    public void Process(MatchRuntime match, long botId, EliminationReason reason, long attackerPlayerId = 0, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false, bool deferGameOver = false, int forcedRank = 0)
    {
        long matchingId = match.MatchingId;
        try
        {
            var eliminatedBot = match.Bots.GetBot(matchingId, botId);
            var eliminatedArea = eliminatedBot?.CurrentArea ?? AreaType.None;
            int finalOrbTier = match.Inventory.GetHighestOrbTier(botId);
            var transition = match.Roster.TryEliminatePlayer(botId, reason,
                attackerPlayerId, eliminatedArea, isAreaClosureElimination, isOvertimeElimination, forcedRank,
                finalOrbTier);
            if (!transition.Applied)
            {
                logger.LogDebug(
                    "Duplicate bot elimination ignored: MatchingId={MatchingId}, BotId={BotId}, Reason={Reason}",
                    matchingId, botId, reason);
                return;
            }

            var affected = transition.AffectedPlayers;
            match.GroundItems.ReleaseClaimReservationsForPlayer(botId);
            eventLogs.LogElimination(
                matchingId,
                botId,
                reason.ToString(),
                isBot: true,
                attackerPlayerId: attackerPlayerId,
                isAreaClosureElimination: isAreaClosureElimination,
                isOvertimeElimination: isOvertimeElimination);
            var matchingSessions = match.Sessions.Values.ToList();
            DropBotInventoryAtCurrentPosition(match, botId, matchingSessions);

            // 1) 전체에게 봇 탈락 알림 (G_TO_C_PLAYER_ELIMINATED)
            using (var eliminatedPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED))
            {
                var eliminatedMsg = new G_TO_C_PLAYER_ELIMINATED
                {
                    PlayerId = botId,
                    AttackerPlayerId = attackerPlayerId,
                    Reason = reason
                };
                eliminatedPacket.SetBody(MessagePackSerializer.Serialize(eliminatedMsg));
                foreach (var s in matchingSessions) s.TrySend(eliminatedPacket);
            }

            // 2) 영향받는 봇/세션 상태 동기화
            foreach (var (affectedId, newStatus) in affected)
            {
                // 봇 영향
                var bot = match.Bots.GetBot(matchingId, affectedId);
                if (bot == null) continue;
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


            // 3) 게임 종료 판정 — 봇 탈락으로 최후 1인 결정 가능
            var (isGameOver, winnerId) = match.Roster.CheckGameOver();
            if (!deferGameOver && isGameOver && matchingSessions.Count > 0)
            {
                logger.LogInformation("게임 종료(봇 탈락 후): MatchingId={MatchingId}, Winner={WinnerId}",
                    matchingId, winnerId);
                matchEliminations.EndMatch(matchingId, winnerId ?? 0, "last_survivor_after_combat");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "봇 탈락 처리 중 오류: BotId={BotId}", botId);
        }
    }

    private void DropBotInventoryAtCurrentPosition(
        MatchRuntime match,
        long botPlayerId,
        IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        long matchingId = match.MatchingId;
        var outcome = EliminationInventoryDropper.DropBotInventoryWithLogs(
            match.Bots, match.Inventory, match.GroundItems, eventLogs,
            matchingId, botPlayerId);
        if (outcome == null)
            return;

        var (bot, drop) = outcome;
        BroadcastGroundItemSpawn(bot.CurrentArea, drop.SpawnedItems,
            matchingSessions.Where(session => session.CurrentArea == bot.CurrentArea));

        logger.LogInformation(
            "Bot elimination inventory scattered: MatchingId={MatchingId}, BotId={BotId}, Area={Area}, ItemCount={ItemCount}",
            matchingId, botPlayerId, bot.CurrentArea, drop.DroppedItemIds.Count);
    }
    private void BroadcastGroundItemSpawn(AreaType area, IReadOnlyList<GroundItemInfo> spawned,
        IEnumerable<GameClientSession> targets)
    {
        if (spawned.Count == 0 || area == AreaType.None)
            return;

        var receivers = targets.ToList();
        if (receivers.Count == 0)
            return;

        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, spawned.ToList());
        foreach (var session in receivers)
            session.TrySend(packet);
    }
}
