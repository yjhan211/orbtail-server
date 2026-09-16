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
    private const float PickupRadius = Config.GROUND_ITEM_PICKUP_RADIUS;
    private const float SummonStonePickupRadius = Config.SUMMON_STONE_PICKUP_RADIUS;
    public const int BandageItemId = 201000008;
    public const int FirstAidKitItemId = 201000018;
    public const int BandageRecovery = 15;
    public const int FirstAidKitRecovery = 35;
    public const int HeartItemId = Config.HEART_GROUND_ITEM_ID;
    private const int HeartRecovery = 105;

    private static bool IsImmediateUseItem(int itemId) => itemId is BandageItemId or HeartItemId;
    public static bool ShouldDropOnElimination(int itemId) => !IsImmediateUseItem(itemId);

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

        if (nextArea != player.GameInfo.ObjectInfo.Area)
        {
            AddReachableItemsInArea(player, match.GroundItems, player.GameInfo.ObjectInfo.Area, from, to, Config.SWARM_MATCH_MAP);
        }
        AddReachableItemsInArea(player, match.GroundItems, nextArea, from, to, nextArea != player.GameInfo.ObjectInfo.Area ? Config.SWARM_MATCH_MAP : null);
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

            float t = pathLengthSquared > 0
                ? Math.Clamp(((item.PositionX - from.X) * pathDx + (item.PositionY - from.Y) * pathDy) / pathLengthSquared, 0f, 1f)
                : 0f;
            var position = new Vector3f(from.X + pathDx * t, from.Y + pathDy * t, 0);
            if (transitionMap is { } map && GameMapData.GetCurrentArea(map, MapCoordinateConverter.WorldToCell(map, position)) != area)
            {
                continue;
            }

            float radius = item.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID ? SummonStonePickupRadius : PickupRadius;
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

        AddReachableItemsInArea(player, match.GroundItems, player.GameInfo.ObjectInfo.Area, player.Position, player.Position);
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
        var items = match.GroundItems;
        var item = items.GetItem(reachable.GroundItemUid);
        if (item == null || items.IsLanding(item.GroundItemUid))
        {
            return false;
        }
        if (item.AreaType != (int)reachable.Area)
        {
            return false;
        }
        float dx = item.PositionX - reachable.Position.X;
        float dy = item.PositionY - reachable.Position.Y;
        float pickupRadius = item.ItemId is Config.SUMMON_STONE_GROUND_ITEM_ID ? SummonStonePickupRadius : PickupRadius;
        if (dx * dx + dy * dy > pickupRadius * pickupRadius)
        {
            return false;
        }
        bool summonStonePickup = item.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID;
        bool bootsPickup = item.ItemId == Config.BOOTS_GROUND_ITEM_ID;
        bool autoUsed = false;
        int healthRecovery = 0;
        InGameItemInfo? addedItem = null;
        if (!summonStonePickup && !bootsPickup)
        {
            if (item.ItemId is Config.KEY_GROUND_ITEM_ID or Config.JAM_GROUND_ITEM_ID)
            {
                return false;
            }
            healthRecovery = item.ItemId switch
            {
                BandageItemId => BandageRecovery,
                FirstAidKitItemId => FirstAidKitRecovery,
                HeartItemId => HeartRecovery,
                _ => 0
            };
            if (item.ItemId == HeartItemId && player.Health >= Config.MAX_HEALTH)
            {
                return false;
            }
            autoUsed = healthRecovery > 0 && IsImmediateUseItem(item.ItemId);
            if (!autoUsed && !match.GetOrbs(player.PlayerId).TryAddOrbWithCapacity(item.ItemId, Config.GetOrbCapacity(), out addedItem))
            {
                return false;
            }
        }

        var claimedItem = items.TakeItem(item.GroundItemUid);
        if (claimedItem == null)
        {
            return false;
        }

        if (bootsPickup)
        {
            if (match.Bots.GetBot(player.PlayerId) is { } bot)
            {
                bot.BootsSpeedUntilUtc = DateTime.UtcNow.AddSeconds(Config.BOOTS_SPEED_DURATION_SECONDS);
            }
        }
        else if (summonStonePickup)
        {
            var summonState = PlayerOrbGrowthService.AddSummonStones(match, player, 1);
            player.Session?.SendSummonStoneState(1, claimedItem.PositionX, claimedItem.PositionY);
        }
        else if (autoUsed)
        {
            healthService.Recover(match, player, healthRecovery);
        }
        else if (addedItem != null)
        {
            player.Session?.SendOrbUpdate(addedItem);

        }

        using var result = PacketMaker.G_TO_C_GROUND_ITEM_PICKUP_RESULT(claimedItem.GroundItemUid, claimedItem.ItemId, true, autoUsed, ErrorCode.SUCCESS);
        player.Session?.TrySend(result);
        return true;
    }
}
