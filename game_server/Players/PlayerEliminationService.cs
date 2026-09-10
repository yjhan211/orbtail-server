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
///     사람과 봇의 탈락을 확정하고 순위 기록·아이템 드롭·참가자 통지·매치 종료 판정을 처리한다.
///     탈락 상태는 Player에 기록하며, 이미 탈락한 참가자는 다시 처리하지 않는다.
///     호출자는 해당 매치의 잠금을 보유해야 한다.
/// </summary>
internal sealed class PlayerEliminationService(
    GameEventLogManager gameEventLogManager,
    MatchResultService matchResults,
    ILogger logger)
{
    public void EliminatePlayer(MatchRuntime runtime, Player eliminatedPlayer, EliminationReason reason, long? causePlayerId = null, bool deferGameOver = false, long attackerPlayerId = 0, int forcedRank = 0)
    {
        long matchingId = runtime.MatchingId;
        long eliminatedPlayerId = eliminatedPlayer.PlayerId;
        var allSessions = runtime.GetSessions();
        var eliminatedSession = eliminatedPlayer.Session;
        var eliminatedBot = runtime.Bots.GetBot(matchingId, eliminatedPlayerId);
        var eliminatedArea = eliminatedPlayer.CurrentArea;
        long resolvedAttackerPlayerId = attackerPlayerId != 0 ? attackerPlayerId : causePlayerId ?? 0;

        int finalOrbTier = runtime.Inventory.GetHighestOrbTier(eliminatedPlayerId);
        bool eliminated = runtime.TryEliminatePlayer(eliminatedPlayerId, reason, resolvedAttackerPlayerId, eliminatedArea, forcedRank, finalOrbTier);
        if (!eliminated)
        {
            logger.LogDebug("Duplicate elimination ignored: matchingId={MatchingId}, PlayerId={PlayerId}, Reason={Reason}", matchingId, eliminatedPlayerId, reason);
            return;
        }

        if (eliminatedBot != null)
        {
            eliminatedBot.Path.Clear();
            eliminatedBot.PathIndex = 0;
            eliminatedBot.LoopWaitUntil = DateTime.MinValue;
        }

        runtime.GroundItems.ReleaseClaimReservationsForPlayer(eliminatedPlayerId);
        gameEventLogManager.LogElimination(matchingId, eliminatedPlayerId, reason.ToString(), isBot: eliminatedBot != null, attackerPlayerId: resolvedAttackerPlayerId);

        var position = eliminatedPlayer.Position;
        if (position != null && eliminatedArea != AreaType.None)
        {
            var drop = EliminationInventoryDropper.DropAll(
                runtime.Inventory, runtime.GroundItems, matchingId, eliminatedPlayerId,
                eliminatedArea, position.X, position.Y, Config.SWARM_MATCH_MAP);
            if (drop.RemovedItems.Count > 0)
            {
                var emptyBoard = runtime.Inventory.GetPlayerInventory(eliminatedPlayerId);
                gameEventLogManager.LogOrbBoardTransition(
                    matchingId, eliminatedPlayerId, emptyBoard.GetAllItems(), 0,
                    eliminatedArea.ToString(), "elimination_drop", isBot: eliminatedBot != null);
                foreach (var item in drop.RemovedItems)
                {
                    eliminatedSession?.SendOrbUpdate(new InGameItemInfo
                    {
                        ItemUid = item.ItemUid,
                        ItemId = item.ItemId,
                        Count = 0,
                        GiftState = item.GiftState
                    });
                }
            }
            if (drop.DroppedItemIds.Count > 0)
            {
                gameEventLogManager.LogEliminationDrop(
                    matchingId, eliminatedPlayerId, eliminatedArea.ToString(),
                    drop.DroppedItemIds, drop.SpawnedItems,
                    GameEventLogManager.CalculateDropRecoveryTotal(drop.DroppedItemIds), isBot: eliminatedBot != null);
                GroundItemNotificationService.BroadcastSpawned(runtime, eliminatedArea, drop.SpawnedItems);
            }
        }

        var eliminatedResultPlayers = matchResults.BuildPlayerResults(matchingId, 0);
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

        using (var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(eliminatedPlayerId))
        {
            foreach (var session in allSessions)
            {
                session.TrySend(leavePacket);
            }
        }

        (bool isGameOver, long? winnerId) = runtime.CheckGameOver();
        if (deferGameOver || !isGameOver || (eliminatedBot != null && allSessions.Count <= 0))
        {
            return;
        }
        matchResults.FinalizeMatch(matchingId, winnerId ?? 0, eliminatedBot == null ? MatchEndReason.LastSurvivor : MatchEndReason.LastSurvivorAfterCombat);
    }
}
