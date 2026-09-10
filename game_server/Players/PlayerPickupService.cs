using game_server.items;
using game_server.logging;
using game_server.matches;
using game_server.players.bots;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.players;

/// <summary>
///     사람과 봇의 이동 경로·현재 위치에서 바닥 아이템 획득 후보를 찾고, 매치 틱에서 획득을 원자적으로 확정한다.
///     획득한 아이템의 효과를 적용하고 결과를 전송하며, 이벤트 로그를 남긴다.
///     획득 후보는 Player가 보관하며, 호출자는 해당 매치 잠금을 보유해야 한다.
/// </summary>
internal sealed class PlayerPickupService(
    GameEventLogManager eventLogs,
    PlayerHealthService healthService,
    ILogger<PlayerPickupService> logger)
{
    private const int MaximumReachableItems = 1024;

    private static Vector3f ClosestPointOnSegment(Vector3f from, Vector3f to, float x, float y)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float lengthSquared = dx * dx + dy * dy;
        float t = lengthSquared > 0 ? Math.Clamp(((x - from.X) * dx + (y - from.Y) * dy) / lengthSquared, 0f, 1f) : 0f;
        return new Vector3f(from.X + dx * t, from.Y + dy * t, 0);
    }

    public static void AddReachableItemsForMovement(MatchRuntime match, Player player, Vector3f from, Vector3f to, AreaType nextArea)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Automatic pickup requires the match lock.");
        }

        if (match.IsEnded || player.PlayerId == 0)
        {
            return;
        }

        if (nextArea != player.CurrentArea)
        {
            AddReachableItemsInArea(player, match.GroundItems, player.CurrentArea, from, to, Config.SWARM_MATCH_MAP);
        }
        AddReachableItemsInArea(player, match.GroundItems, nextArea, from, to, nextArea != player.CurrentArea ? Config.SWARM_MATCH_MAP : null);
    }

    public static void AddReachableItemsInArea(Player player, GroundItemManager items, AreaType area, Vector3f from, Vector3f to, MapId? transitionMap = null)
    {
        if (area == AreaType.None)
        {
            return;
        }
        var reachable = player.ReachableItems;
        foreach (var item in items.GetSnapshot(area))
        {
            if (items.IsLanding(item.GroundItemUid))
            {
                continue;
            }

            if (item.SourcePlayerId == player.PlayerId && player.PlayerId != 0)
            {
                continue;
            }
            var position = ClosestPointOnSegment(from, to, item.PositionX, item.PositionY);
            if (transitionMap is { } map &&
                GameMapData.GetCurrentArea(map, MapCoordinateConverter.WorldToCell(map, position)) != area)
            {
                continue;
            }

            float radius = item.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID ? GroundItemManager.SummonStonePickupRadius : GroundItemManager.PickupRadius;
            float dx = item.PositionX - position.X;
            float dy = item.PositionY - position.Y;
            if (dx * dx + dy * dy > radius * radius)
            {
                continue;
            }

            if (reachable.Count >= MaximumReachableItems && !reachable.ContainsKey(item.GroundItemUid))
            {
                continue;
            }
            reachable.TryAdd(item.GroundItemUid, new Player.ReachableItem(item.GroundItemUid, area, position));
        }
    }

    internal static Player.ReachableItem[] TakeReachableItems(Player player)
    {
        var items = player.ReachableItems.Values.ToArray();
        player.ReachableItems.Clear();
        return items;
    }

    public void PickUp(MatchRuntime match, IReadOnlyCollection<Player> players)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Automatic pickup requires the match lock.");
        }

        foreach (var stale in match.GetPlayers())
        {
            if (!players.Contains(stale))
            {
                stale.ReachableItems.Clear();
            }
        }

        foreach (var player in players)
        {
            if (match.IsEnded)
            {
                return;
            }
            try
            {
                PickUp(match, player);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic pickup failed: MatchingId={MatchingId}, PlayerId={PlayerId}", match.MatchingId, player.PlayerId);
            }
        }
    }

    public void PickUp(MatchRuntime match, Player player)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Automatic pickup requires the match lock.");
        }

        if (match.IsEnded || player.PlayerId == 0 || match.MatchingId <= 0 || player.IsEliminated || player.Position == null || player is { PlayerId: < 0, IsSleeping: true })
        {
            player.ReachableItems.Clear();
            return;
        }

        AddReachableItemsInArea(player, match.GroundItems, player.CurrentArea, player.Position, player.Position);
        foreach (var reachable in TakeReachableItems(player))
        {
            if (match.IsEnded)
            {
                break;
            }

            bool pickedUp = TryPickUpItem(match, player, reachable);
            if (pickedUp && player.PlayerId < 0)
            {
                break;
            }
        }
    }

    private bool TryPickUpItem(MatchRuntime match, Player player, Player.ReachableItem reachable)
    {
        var item = match.GroundItems.GetItem(reachable.GroundItemUid);
        if (player.PlayerId < 0 &&
            item is { ItemId: Config.SUMMON_STONE_GROUND_ITEM_ID or Config.BOOTS_GROUND_ITEM_ID } &&
            match.GroundItems.IsYoungerThan(item.GroundItemUid, BotPlayerManager.SummonStoneBotReactionDelay))
        {
            return false;
        }

        InGameItemInfo? addedItem = null;
        bool autoUsed = false;
        bool summonStonePickup = false;
        bool bootsPickup = false;
        int healthRecovery = 0;
        long discovererPlayerId = match.GroundItems.GetDiscovererPlayerId(reachable.GroundItemUid);

        var status = match.GroundItems.TryClaim(
            reachable.GroundItemUid,
            player.PlayerId,
            reachable.Area,
            reachable.Position.X,
            reachable.Position.Y,
            groundItem =>
            {
                if (groundItem.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID)
                {
                    summonStonePickup = true;
                    return true;
                }

                if (groundItem.ItemId == Config.BOOTS_GROUND_ITEM_ID)
                {
                    bootsPickup = true;
                    return true;
                }

                if (groundItem.ItemId == Config.KEY_GROUND_ITEM_ID)
                {
                    return false;
                }

                var disposition = GroundItemPolicy.Resolve(
                    groundItem.ItemId,
                    player.Health,
                    out healthRecovery,
                    match.MatchingId,
                    player.PlayerId);
                if (disposition == GroundItemDisposition.LeaveOnGround)
                {
                    return false;
                }

                if (disposition == GroundItemDisposition.AutoUse)
                {
                    autoUsed = true;
                    return true;
                }

                return match.Inventory.GetPlayerInventory(player.PlayerId).TryAddItemWithCapacity(
                    groundItem.ItemId,
                    Config.GetOrbCapacity(),
                    out addedItem);
            },
            out var claimedItem);

        if (status != GroundItemClaimStatus.Success || claimedItem == null)
        {
            return false;
        }

        if (bootsPickup)
        {
            if (match.Bots.GetBot(match.MatchingId, player.PlayerId) is { } bot)
            {
                bot.BootsSpeedUntilUtc = DateTime.UtcNow.AddSeconds(Config.BOOTS_SPEED_DURATION_SECONDS);
            }
        }
        else if (summonStonePickup)
        {
            var summonState = PlayerOrbGrowthService.AddSummonStones(match, player, 1);
            player.Session?.SendSummonStoneState(1, claimedItem.PositionX, claimedItem.PositionY);
            eventLogs.LogSummonStoneAward(match.MatchingId, player.PlayerId, monsterId: 0, amount: 1, summonState.StoneCount, player.CurrentArea.ToString(), isCore: false, isBot: player.PlayerId < 0);
        }
        else if (autoUsed)
        {
            var change = healthService.Recover(match, player, healthRecovery);
            int requested = healthRecovery;
            int recovered = change.Recovered;
            eventLogs.LogRecoveryUse(match.MatchingId, player.PlayerId, claimedItem.ItemId, recovered, source: "ground_auto_use", isBot: player.PlayerId < 0);
            eventLogs.LogPelletPickupOutcome(match.MatchingId, player.PlayerId, claimedItem.ItemId, requested, recovered, recovered == 0 ? "wasted" : recovered == requested ? "effective" : "partial_waste", isBot: player.PlayerId < 0);
        }
        else if (addedItem != null)
        {
            player.Session?.SendOrbUpdate(addedItem);

        }

        using (var removed = PacketMaker.G_TO_C_GROUND_ITEM_REMOVED(claimedItem.GroundItemUid, player.PlayerId, autoUsed))
        {
            var targetSessions = new List<GameClientSession>();
            foreach (var other in match.GetSessions())
            {
                if (!other.Player.IsEliminated && other.Player.CurrentArea == (AreaType)claimedItem.AreaType)
                {
                    targetSessions.Add(other);
                }
            }

            foreach (var other in targetSessions)
            {
                other.TrySend(removed);
            }
        }
        eventLogs.LogGroundItemPickup(match.MatchingId, player.PlayerId, discovererPlayerId, claimedItem.GroundItemUid, claimedItem.ItemId, player.CurrentArea.ToString(), autoUsed, isBot: player.PlayerId < 0);
        if (!summonStonePickup && !bootsPickup)
        {
            var boardAfterPickup = match.Inventory.GetPlayerInventory(player.PlayerId);
            eventLogs.LogOrbBoardTransition(match.MatchingId, player.PlayerId, boardAfterPickup.GetAllItems(), boardAfterPickup.GetOrderedOrbs().FirstOrDefault()?.ItemId ?? 0, player.CurrentArea.ToString(), "pickup", isBot: player.PlayerId < 0);
        }
        using var result = PacketMaker.G_TO_C_GROUND_ITEM_PICKUP_RESULT(claimedItem.GroundItemUid, claimedItem.ItemId, true, autoUsed, ErrorCode.SUCCESS);
        player.Session?.TrySend(result);
        return true;
    }
}
