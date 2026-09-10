using game_server.logging;
using game_server.matches;
using game_server.sessions;
using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.items;

/// <summary>
/// 승인된 이동 경로와 현재 위치에서 자동 획득을 처리하고 상태 변경과 결과를 전송한다.
/// 후보는 매치가 참가자별로 소유하며, 모든 호출은 해당 매치 잠금 안에서 실행한다.
/// </summary>
internal sealed class GroundItemAutoPickupService(
    GameEventLogManager eventLogs,
    ILogger<GroundItemAutoPickupService> logger)
{
    public static void RecordMovement(MatchRuntime match, Player player, Vector3f from, Vector3f to, AreaType nextArea)
    {
        RequireMatchLock(match);
        if (match.IsEnded || player.PlayerId == 0) return;
        var candidates = GetCandidates(match, player);
        if (nextArea != player.CurrentArea)
            candidates.Record(match.GroundItems, player.PlayerId, player.CurrentArea,
                from, to, Config.SWARM_MATCH_MAP);
        candidates.Record(match.GroundItems, player.PlayerId, nextArea,
            from, to, nextArea != player.CurrentArea ? Config.SWARM_MATCH_MAP : null);
    }

    public void Process(MatchRuntime match, IReadOnlyCollection<Player> players)
    {
        RequireMatchLock(match);
        // 처리 대상에서 빠진 참가자의 후보는 버린다.
        foreach (var stale in match.GroundItemPickupCandidates.Keys.Except(players).ToArray())
            match.GroundItemPickupCandidates.Remove(stale);
        foreach (var player in players)
        {
            if (match.IsEnded) return;
            try
            {
                Process(match, player);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic pickup failed: MatchingId={MatchingId}, PlayerId={PlayerId}",
                    match.MatchingId, player.PlayerId);
            }
        }
    }

    public void Process(MatchRuntime match, Player player)
    {
        RequireMatchLock(match);
        if (match.IsEnded || player.PlayerId == 0 || match.MatchingId <= 0 ||
            player.IsEliminated || player.Position == null)
        {
            match.GroundItemPickupCandidates.Remove(player);
            return;
        }
        if (player.PlayerId < 0 && player.IsSleeping)
        {
            match.GroundItemPickupCandidates.Remove(player);
            return;
        }
        var candidates = GetCandidates(match, player);
        candidates.Record(match.GroundItems, player.PlayerId, player.CurrentArea,
            player.Position, player.Position);
        match.GroundItemPickupCandidates.Remove(player);
        foreach (var candidate in candidates.Take())
        {
            if (match.IsEnded) break;
            var item = match.GroundItems.GetItem(candidate.GroundItemUid);
            if (player.PlayerId < 0 && item != null &&
                item.ItemId is Config.SUMMON_STONE_GROUND_ITEM_ID or Config.BOOTS_GROUND_ITEM_ID &&
                match.GroundItems.IsYoungerThan(item.GroundItemUid, BotPlayerManager.SummonStoneBotReactionDelay))
                continue;
            var pickup = GroundItemPickupService.TryPickup(match, player.PlayerId, candidate.Area,
                candidate.Position, player.Health, candidate.GroundItemUid);
            if (pickup.Status != GroundItemClaimStatus.Success || pickup.ClaimedItem == null) continue;
            ApplyGroundItemPickup(match, player, pickup);
            if (player.PlayerId < 0) break;
        }
    }

    private static GroundItemPickupCandidates GetCandidates(MatchRuntime match, Player player)
    {
        if (!match.GroundItemPickupCandidates.TryGetValue(player, out var candidates))
            match.GroundItemPickupCandidates.Add(player, candidates = new GroundItemPickupCandidates());
        return candidates;
    }

    private static void RequireMatchLock(MatchRuntime match)
    {
        if (!Monitor.IsEntered(match.MatchLock))
            throw new InvalidOperationException("Automatic pickup requires the match lock.");
    }

    private void ApplyGroundItemPickup(MatchRuntime match, Player player, GroundItemPickupResult pickup)
    {
        var claimedItem = pickup.ClaimedItem!;
        var addedItem = pickup.AddedItem;

        if (pickup.BootsPickup)
        {
            if (match.Bots.GetBot(match.MatchingId, player.PlayerId) is { } bot)
                bot.BootsSpeedUntilUtc = DateTime.UtcNow.AddSeconds(Config.BOOTS_SPEED_DURATION_SECONDS);
            // 부츠 (#222 M4): 이속은 클라 이동이 소유한다 — 서버는 픽업 결과만 확정.
            // 클라가 픽업 결과(ItemId)로 10초 버프·HUD 타이머를 시작한다.
        }
        else if (pickup.SummonStonePickup)
        {
            var summonState = match.SummonStones.AddStones(player.PlayerId, 1);
            player.Session?.SendSummonStoneState(1, claimedItem.PositionX, claimedItem.PositionY);
            eventLogs.LogSummonStoneAward(
                match.MatchingId,
                player.PlayerId,
                monsterId: 0,
                amount: 1,
                summonState.StoneCount,
                player.CurrentArea.ToString(),
                isCore: false,
                isBot: player.PlayerId < 0);
        }
        else if (pickup.AutoUsed)
        {
            int effectiveHealthRecovery = Math.Min(pickup.HealthRecovery, Math.Max(0, Config.MAX_HEALTH - player.Health));
            int requestedRecovery = pickup.HealthRecovery;
            int effectiveRecovery = effectiveHealthRecovery;
            PlayerHealthChangeService.Record(match.MatchingId, player, player.Recover(pickup.HealthRecovery), eventLogs, logger);
            eventLogs.LogRecoveryUse(
                match.MatchingId, player.PlayerId, claimedItem.ItemId,
                effectiveRecovery, source: "ground_auto_use", isBot: player.PlayerId < 0);
            eventLogs.LogPelletPickupOutcome(
                match.MatchingId, player.PlayerId, claimedItem.ItemId, requestedRecovery, effectiveRecovery,
                effectiveRecovery == 0 ? "wasted" : effectiveRecovery == requestedRecovery ? "effective" : "partial_waste",
                isBot: player.PlayerId < 0);
        }
        else if (addedItem != null)
        {
            player.Session?.SendOrbUpdate(addedItem);

        }

        using (var removed = PacketMaker.G_TO_C_GROUND_ITEM_REMOVED(
                   claimedItem.GroundItemUid, player.PlayerId, pickup.AutoUsed))
        {
            var targetSessions = new List<GameClientSession>();
            foreach (var other in match.GetSessions())
            {
                if (!other.Player.IsEliminated && other.Player.CurrentArea == (AreaType)claimedItem.AreaType)
                    targetSessions.Add(other);
            }
            foreach (var other in targetSessions)
                other.TrySend(removed);
        }
        eventLogs.LogGroundItemPickup(
            match.MatchingId,
            player.PlayerId,
            pickup.DiscovererPlayerId,
            claimedItem.GroundItemUid,
            claimedItem.ItemId,
            player.CurrentArea.ToString(),
            pickup.AutoUsed,
            isBot: player.PlayerId < 0);
        if (!pickup.SummonStonePickup && !pickup.BootsPickup)
        {
            var boardAfterPickup = match.Inventory.GetPlayerInventory(player.PlayerId);
            eventLogs.LogOrbBoardTransition(
                match.MatchingId, player.PlayerId, boardAfterPickup.GetAllItems(),
                boardAfterPickup.GetOrderedOrbs().FirstOrDefault()?.ItemId ?? 0, player.CurrentArea.ToString(), "pickup", isBot: player.PlayerId < 0);
        }
        using var result = PacketMaker.G_TO_C_GROUND_ITEM_PICKUP_RESULT(
            claimedItem.GroundItemUid, claimedItem.ItemId, true, pickup.AutoUsed, ErrorCode.SUCCESS);
        player.Session?.TrySend(result);
    }
}
