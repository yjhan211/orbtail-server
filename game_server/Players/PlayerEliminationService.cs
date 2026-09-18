using game_server.matches;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     사람과 봇의 탈락을 확정하고 순위 기록·아이템 드롭·참가자 통지·매치 종료 판정을 처리한다.
///     탈락 상태는 Player에 기록하며, 이미 탈락한 참가자는 다시 처리하지 않는다.
///     호출자는 해당 매치의 잠금을 보유해야 한다.
/// </summary>
internal sealed class PlayerEliminationService(MatchResultService matchResults, MatchSynchronizationService synchronization, ILogger logger)
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
        var eliminatedBot = runtime.Bots.GetBot(eliminatedPlayerId);
        var eliminatedArea = eliminatedPlayer.CurrentArea;
        long resolvedAttackerPlayerId = attackerPlayerId != 0 ? attackerPlayerId : causePlayerId ?? 0;

        bool eliminated = runtime.TryEliminatePlayer(eliminatedPlayerId, reason, resolvedAttackerPlayerId, eliminatedArea, forcedRank);
        if (!eliminated)
        {
            logger.LogDebug("Duplicate elimination ignored: matchingId={MatchingId}, PlayerId={PlayerId}, Reason={Reason}", matchingId, eliminatedPlayerId, reason);
            return;
        }

        if (eliminatedBot != null)
        {
            eliminatedBot.Movement.Clear();
        }

        logger.LogInformation("Player eliminated: MatchingId={MatchingId}, PlayerId={PlayerId}, Reason={Reason}, AttackerPlayerId={AttackerPlayerId}", runtime.MatchingId, eliminatedPlayerId, reason, resolvedAttackerPlayerId);

        var eliminatedSession = eliminatedPlayer.Session;
        var clearedOrbs = new List<InGameItemInfo>();
        var position = eliminatedPlayer.Position;
        if (position != null && eliminatedArea != AreaType.None)
        {
            var removedItems = eliminatedPlayer.Orbs.TakeAllOrbs();
            eliminatedPlayer.Orbs.ClearAttackTimers();
            var droppedItemIds = new List<int>();
            foreach (var item in removedItems)
            {
                for (int count = 0; count < item.Count; count++)
                {
                    droppedItemIds.Add(item.ItemId);
                }
            }

            runtime.GroundItems.SpawnItems(
                eliminatedArea,
                position.X,
                position.Y,
                droppedItemIds);

            foreach (var item in removedItems)
            {
                clearedOrbs.Add(new InGameItemInfo
                {
                    ItemUid = item.ItemUid,
                    ItemId = item.ItemId,
                    Count = 0,
                    GiftState = item.GiftState
                });
            }
        }

        synchronization.QueuePlayerElimination(runtime, new PlayerEliminationInfo
        {
            PlayerId = eliminatedPlayerId,
            AttackerPlayerId = resolvedAttackerPlayerId,
            Reason = reason
        });

        if (eliminatedSession != null)
        {
            if (clearedOrbs.Count > 0)
            {
                synchronization.QueuePacket(runtime, eliminatedSession, Protocol.G_TO_C_ORB_UPDATE, new G_TO_C_ORB_UPDATE { Items = clearedOrbs });
            }
            synchronization.QueuePacket(runtime, eliminatedSession, Protocol.G_TO_C_ELIMINATION_RESULT, new G_TO_C_ELIMINATION_RESULT { Players = matchResults.BuildPlayerResults(runtime, 0) });
        }

        (bool isGameOver, long? winnerId) = runtime.CheckGameOver();
        if (deferGameOver || !isGameOver || (eliminatedBot != null && allSessions.Count <= 0))
        {
            return;
        }
        matchResults.FinalizeMatch(runtime, winnerId ?? 0, eliminatedBot == null ? MatchEndReason.LastSurvivor : MatchEndReason.LastSurvivorAfterCombat);
    }
}
