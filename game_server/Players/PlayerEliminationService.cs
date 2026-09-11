using game_server.matches;
using game_server.matches.logging;
using game_server.matches.results;
using game_server.sessions;
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
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Player elimination requires the match lock.");
        }

        long matchingId = runtime.MatchingId;
        long eliminatedPlayerId = eliminatedPlayer.PlayerId;
        var allSessions = runtime.GetSessions();
        var eliminatedSession = eliminatedPlayer.Session;
        var eliminatedBot = runtime.Bots.GetBot(eliminatedPlayerId);
        var eliminatedArea = eliminatedPlayer.CurrentArea;
        long resolvedAttackerPlayerId = attackerPlayerId != 0 ? attackerPlayerId : causePlayerId ?? 0;

        int finalOrbTier = eliminatedPlayer.Orbs.GetHighestOrbTier();
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

        gameEventLogManager.LogElimination(matchingId, eliminatedPlayerId, reason.ToString(), isBot: eliminatedBot != null, attackerPlayerId: resolvedAttackerPlayerId);

        var position = eliminatedPlayer.Position;
        if (position != null && eliminatedArea != AreaType.None)
        {
            var removedItems = eliminatedPlayer.Orbs.TakeAllItems();
            eliminatedPlayer.ClearOrbTimers();
            var droppedItemIds = new List<int>();
            foreach (var item in removedItems)
            {
                if (!MatchGroundItemState.ShouldDropOnElimination(item.ItemId))
                {
                    continue;
                }

                for (int count = 0; count < item.Count; count++)
                {
                    droppedItemIds.Add(item.ItemId);
                }
            }

            var spawnedItems = runtime.GroundItems.SpawnItems(
                eliminatedArea,
                position.X,
                position.Y,
                droppedItemIds,
                mapId: Config.SWARM_MATCH_MAP,
                layout: GroundItemSpawnLayout.EliminationScatter);

            if (removedItems.Count > 0)
            {
                var emptyBoard = eliminatedPlayer.Orbs;
                gameEventLogManager.LogOrbBoardTransition(matchingId, eliminatedPlayerId, emptyBoard.GetAllItems(), 0, eliminatedArea.ToString(), "elimination_drop", isBot: eliminatedBot != null);
                foreach (var item in removedItems)
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
            if (droppedItemIds.Count > 0)
            {
                gameEventLogManager.LogEliminationDrop(matchingId, eliminatedPlayerId, eliminatedArea.ToString(), droppedItemIds, spawnedItems, GameEventLogManager.CalculateDropRecoveryTotal(droppedItemIds), isBot: eliminatedBot != null);
                if (spawnedItems.Count > 0)
                {
                    var targetSessions = new List<GameClientSession>();
                    foreach (var session in runtime.GetSessions())
                    {
                        if (!session.Player.IsEliminated && session.Player.CurrentArea == eliminatedArea)
                        {
                            targetSessions.Add(session);
                        }
                    }

                    using var spawnedPacket = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)eliminatedArea, spawnedItems.ToList());
                    foreach (var session in targetSessions)
                    {
                        session.TrySend(spawnedPacket);
                    }
                }
            }
        }

        var eliminatedResultPlayers = matchResults.BuildPlayerResults(runtime, 0);
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
