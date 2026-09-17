using network.common.data;
using game_server.matches;
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
internal sealed class PlayerEliminationService(MatchResultService matchResults, ILogger logger)
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

            if (removedItems.Count > 0)
            {
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

        // 이 타격으로 바로 매치가 종료될 수 있으므로 퇴장도 여기서 확정한다.
        foreach (var session in allSessions)
        {
            session.SendObjectLeaves([new ObjectIdentity { Type = ObjectType.PLAYER, Id = eliminatedPlayerId }]);
        }

        (bool isGameOver, long? winnerId) = runtime.CheckGameOver();
        if (deferGameOver || !isGameOver || (eliminatedBot != null && allSessions.Count <= 0))
        {
            return;
        }
        matchResults.FinalizeMatch(runtime, winnerId ?? 0, eliminatedBot == null ? MatchEndReason.LastSurvivor : MatchEndReason.LastSurvivorAfterCombat);
    }
}
