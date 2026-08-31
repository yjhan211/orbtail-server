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
        MapId? mapId = null)
    {
        mapId ??= Config.SWARM_MATCH_MAP;
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
            mapId: mapId.Value,
            layout: GroundItemSpawnLayout.EliminationScatter);

        return new EliminationInventoryDropResult(removedItems, droppedItemIds, spawnedItems);
    }

    /// <summary>
    ///     봇 탈락 드롭 공용 경로 (#297 중복 단일화): 봇 조회 → 드롭 → 보드/드롭 로그까지 한 번에.
    ///     브로드캐스트할 것이 없으면 null — 호출처는 결과가 있을 때만 스폰을 뿌린다.
    /// </summary>
    public static BotEliminationDropOutcome? DropBotInventoryWithLogs(
        BotPlayerManager botPlayerManager,
        InGameInventoryManager inventoryManager,
        GroundItemManager groundItemManager,
        GameEventLogManager gameEventLogManager,
        long matchingId,
        long botPlayerId)
    {
        var bot = botPlayerManager.GetBot(matchingId, botPlayerId);
        if (bot == null || bot.CurrentArea == AreaType.None)
            return null;

        var drop = DropAll(
            inventoryManager,
            groundItemManager,
            matchingId,
            botPlayerId,
            bot.CurrentArea,
            bot.Position.X,
            bot.Position.Y,
            botPlayerManager.GetMatchingMapId(matchingId));
        if (drop.RemovedItems.Count == 0)
            return null;

        var emptyBoard = inventoryManager.GetPlayerInventory(matchingId, botPlayerId);
        gameEventLogManager.LogOrbBoardTransition(
            matchingId, botPlayerId, emptyBoard.GetAllItems(), 0, bot.CurrentArea.ToString(), "elimination_drop",
            isBot: true);

        if (drop.DroppedItemIds.Count == 0)
            return null;

        gameEventLogManager.LogEliminationDrop(
            matchingId,
            botPlayerId,
            bot.CurrentArea.ToString(),
            drop.DroppedItemIds,
            drop.SpawnedItems,
            GameEventLogManager.CalculateDropRecoveryTotal(drop.DroppedItemIds),
            isBot: true);

        return new BotEliminationDropOutcome(bot, drop);
    }
}

public sealed record BotEliminationDropOutcome(
    BotPlayerState Bot,
    EliminationInventoryDropResult Drop);
