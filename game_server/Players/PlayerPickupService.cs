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
internal sealed class PlayerPickupService(PlayerHealthService healthService, ILogger<PlayerPickupService> logger)
{
    private const int MaximumReachableItems = 1024;

    private static float GetPickupRadius(int itemId) =>
        itemId == Config.SUMMON_STONE_GROUND_ITEM_ID ? Config.SUMMON_STONE_PICKUP_RADIUS : Config.GROUND_ITEM_PICKUP_RADIUS;

    public static void AddReachableItemsForMovement(MatchRuntime match, Player player, Vector3f from, Vector3f to, AreaType nextArea)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Automatic pickup requires the match lock.");
        }
        if (match.IsEnded)
        {
            return;
        }

        var currentArea = GameMapData.GetCurrentArea(player.GameInfo.ObjectInfo.MapId, player.GameInfo.ObjectInfo.Cell);
        if (nextArea == currentArea)
        {
            AddReachableItemsInArea(player, match.GroundItems, nextArea, from, to);
            return;
        }

        AddReachableItemsInArea(player, match.GroundItems, currentArea, from, to, Config.SWARM_MATCH_MAP);
        AddReachableItemsInArea(player, match.GroundItems, nextArea, from, to, Config.SWARM_MATCH_MAP);
    }

    public static void AddReachableItemsInArea(Player player, MatchGroundItemState items, AreaType area, Vector3f from, Vector3f to, MapId? transitionMap = null)
    {
        if (area == AreaType.None)
        {
            return;
        }
        var reachable = player.ReachableItems;
        float pathDx = to.X - from.X;
        float pathDy = to.Y - from.Y;
        float pathLengthSquared = pathDx * pathDx + pathDy * pathDy;
        foreach (var item in items.GetItemsInArea(area))
        {
            if (items.IsLanding(item.GroundItemUid))
            {
                continue;
            }

            float t = pathLengthSquared > 0 ? Math.Clamp(((item.PositionX - from.X) * pathDx + (item.PositionY - from.Y) * pathDy) / pathLengthSquared, 0f, 1f) : 0f;
            var position = new Vector3f(from.X + pathDx * t, from.Y + pathDy * t, 0);
            if (transitionMap is { } map && GameMapData.GetCurrentArea(map, MapCoordinateConverter.WorldToCell(map, position)) != area)
            {
                continue;
            }

            float radius = GetPickupRadius(item.ItemId);
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

    public void ProcessTick(MatchRuntime match, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Automatic pickup requires the match lock.");
        }
        if (!match.IsGameplayActive(nowUtc))
        {
            return;
        }

        foreach (var player in match.GetPlayers())
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

        if (match.IsEnded || player.IsEliminated || player.Position == null || player is { PlayerId: < 0, IsSleeping: true })
        {
            player.ReachableItems.Clear();
            return;
        }

        AddReachableItemsInArea(player, match.GroundItems, GameMapData.GetCurrentArea(player.GameInfo.ObjectInfo.MapId, player.GameInfo.ObjectInfo.Cell), player.Position, player.Position);
        Player.ReachableItem[] candidates = [.. player.ReachableItems.Values];
        player.ReachableItems.Clear();
        foreach (var candidate in candidates)
        {
            if (match.IsEnded)
            {
                break;
            }
            TryPickUpItem(match, player, candidate);
        }
    }

    private bool TryPickUpItem(MatchRuntime match, Player player, Player.ReachableItem reachable)
    {
        var items = match.GroundItems;
        var item = items.GetItem(reachable.GroundItemUid);
        if (item == null || items.IsLanding(item.GroundItemUid) || item.AreaType != (int)reachable.Area)
        {
            return false;
        }
        float dx = item.PositionX - reachable.Position.X;
        float dy = item.PositionY - reachable.Position.Y;
        float radius = GetPickupRadius(item.ItemId);
        if (dx * dx + dy * dy > radius * radius)
        {
            return false;
        }

        bool isSummonStone = item.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID;
        bool isHeart = item.ItemId == Config.HEART_GROUND_ITEM_ID;
        InGameItemInfo? addedOrb = null;
        if (isHeart)
        {
            if (player.Health >= Config.MAX_HEALTH)
            {
                return false;
            }
        }
        else if (!isSummonStone && !match.GetOrbs(player.PlayerId).TryAddOrbWithCapacity(item.ItemId, Config.GetOrbCapacity(), out addedOrb))
        {
            return false;
        }

        var claimedItem = items.TakeItem(item.GroundItemUid);
        if (claimedItem == null)
        {
            return false;
        }

        if (isSummonStone)
        {
            PlayerOrbGrowthService.AddSummonStones(match, player, 1);
            player.Session?.SendSummonStoneState(1, claimedItem.PositionX, claimedItem.PositionY);
        }
        else if (isHeart)
        {
            healthService.Recover(match, player, Config.SWARM_HEART_RECOVERY);
        }
        else if (addedOrb != null)
        {
            player.Session?.SendOrbUpdate(addedOrb);
        }

        using var result = PacketMaker.G_TO_C_GROUND_ITEM_PICKUP_RESULT(claimedItem.GroundItemUid, claimedItem.ItemId, true, isHeart, ErrorCode.SUCCESS);
        player.Session?.TrySend(result);
        return true;
    }
}
