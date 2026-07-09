using System.Collections.Concurrent;
using network.common.data;

namespace game_server.services;

public sealed class AreaItemStockManager
{
    private const int DropChancePercent = 90;
    private readonly ConcurrentDictionary<long, MatchingAreaItemStock> _matchingStocks = new();

    public bool TryConsumeDrop(long matchingId, int areaType, out int itemId)
    {
        itemId = 0;
        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());

        lock (stock.SyncRoot)
        {
            if (Random.Shared.Next(100) >= DropChancePercent)
                return false;

            var areaStock = stock.GetOrCreateAreaStock(areaType);
            if (areaStock.Count == 0)
                return false;

            int index = Random.Shared.Next(areaStock.Count);
            itemId = areaStock[index];
            areaStock.RemoveAt(index);
            return itemId > 0;
        }
    }

    public IReadOnlyDictionary<int, int> GetRemainingSnapshot(long matchingId, int areaType)
    {
        if (!_matchingStocks.TryGetValue(matchingId, out var stock))
            return new Dictionary<int, int>();

        lock (stock.SyncRoot)
        {
            return stock.GetOrCreateAreaStock(areaType)
                .GroupBy(id => id)
                .ToDictionary(group => group.Key, group => group.Count());
        }
    }

    public void RemoveMatchingState(long matchingId)
    {
        _matchingStocks.TryRemove(matchingId, out _);
    }

    private sealed class MatchingAreaItemStock
    {
        private readonly Dictionary<int, List<int>> _areaStocks = new();
        public object SyncRoot { get; } = new();

        public List<int> GetOrCreateAreaStock(int areaType)
        {
            if (_areaStocks.TryGetValue(areaType, out var stock))
                return stock;

            stock = GameInteractableData.GetItemPoolByArea(areaType)
                .Where(IsBattleLootDropItem)
                .ToList();
            _areaStocks[areaType] = stock;
            return stock;
        }

        private static bool IsBattleLootDropItem(int itemId)
        {
            var item = GameItemData.Get(itemId);
            return item != null
                   && !BattleItemRecipeData.IsRecipeOutputItem(itemId)
                   && (item.Type == global::network.common.ItemType.CONSUMABLE || item.Type == global::network.common.ItemType.MATERIAL);
        }
    }
}
