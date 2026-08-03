using network.common;
using network.common.data.models;

namespace game_server.services;

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
        MapId mapId = MapId.School)
    {
        var removedItems = inventoryManager.TakeAllItems(matchingId, playerId);
        var droppedItemIds = removedItems
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count))
            .Where(GroundItemPickupPolicy.ShouldDropOnElimination)
            .ToList();
        var spawnedItems = groundItemManager.SpawnItems(
            matchingId,
            area,
            originX,
            originY,
            droppedItemIds,
            mapId: mapId,
            layout: GroundItemSpawnLayout.EliminationScatter);

        return new EliminationInventoryDropResult(removedItems, droppedItemIds, spawnedItems);
    }
}
