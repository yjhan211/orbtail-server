using network.common;
using network.common.data.models;

namespace game_server.items;

public sealed record EliminationInventoryDropResult(
    List<InGameItemInfo> RemovedItems,
    List<int> DroppedItemIds,
    List<GroundItemInfo> SpawnedItems);

/// <summary>
/// Removes an eliminated actor's board once and scatters every droppable item around the actor.
/// Player and bot eliminations share this path so their loot rules cannot drift apart.
/// </summary>
public static class EliminationInventoryDropper
{
    public static EliminationInventoryDropResult DropAll(
        InGameInventoryManager inventoryManager,
        GroundItemManager groundItemManager,
        long matchingId,
        long playerId,
        AreaType area,
        float originX,
        float originY,
        MapId? mapId = null)
    {
        mapId ??= Config.SWARM_MATCH_MAP;
        var removedItems = inventoryManager.TakeAllItems(playerId);
        var droppedItemIds = removedItems
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count))
            .Where(GroundItemPickupPolicy.ShouldDropOnElimination)
            .ToList();
        var spawnedItems = groundItemManager.SpawnItems(
            area,
            originX,
            originY,
            droppedItemIds,
            mapId: mapId.Value,
            layout: GroundItemSpawnLayout.EliminationScatter);

        return new EliminationInventoryDropResult(removedItems, droppedItemIds, spawnedItems);
    }
}
