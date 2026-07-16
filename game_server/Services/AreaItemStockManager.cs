using System.Collections.Concurrent;
using network.common;
using network.common.data;

namespace game_server.services;

public sealed class AreaItemStockManager
{
    private readonly ConcurrentDictionary<long, MatchingAreaItemStock> _matchingStocks = new();

    public void InitializeMatching(long matchingId)
    {
        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
        {
            foreach (var area in Enum.GetValues<AreaType>())
            {
                if (area == AreaType.None) continue;
                stock.GetOrCreateAreaStock((int)area);
            }
        }
    }

    public bool HasRemaining(long matchingId, int areaType)
    {
        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
            return stock.GetOrCreateAreaStock(areaType).Count > 0;
    }

    public bool TryConsumeDrops(long matchingId, int areaType, int maxCount, out List<int> itemIds)
    {
        itemIds = new List<int>();
        if (maxCount <= 0) return false;

        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
        {
            var areaStock = stock.GetOrCreateAreaStock(areaType);
            int count = Math.Min(maxCount, areaStock.Count);
            for (int i = 0; i < count; i++)
            {
                int index = Random.Shared.Next(areaStock.Count);
                itemIds.Add(areaStock[index]);
                areaStock.RemoveAt(index);
            }
            return itemIds.Count > 0;
        }
    }

    public bool TryConsumeDrop(long matchingId, int areaType, out int itemId)
    {
        bool consumed = TryConsumeDrops(matchingId, areaType, 1, out var items);
        itemId = consumed ? items[0] : 0;
        return consumed;
    }

    public int GetRemainingCount(long matchingId, int areaType)
    {
        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
            return stock.GetOrCreateAreaStock(areaType).Count;
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

    public void RemoveMatchingState(long matchingId) => _matchingStocks.TryRemove(matchingId, out _);

    private sealed class MatchingAreaItemStock
    {
        private readonly Dictionary<int, List<int>> _areaStocks = new();
        public object SyncRoot { get; } = new();

        public List<int> GetOrCreateAreaStock(int areaType)
        {
            if (_areaStocks.TryGetValue(areaType, out var stock)) return stock;

            stock = GameInteractableData.GetItemPoolByArea(areaType)
                .Where(IsSurvivorLootItem)
                .ToList();
            _areaStocks[areaType] = stock;
            return stock;
        }

        private static bool IsSurvivorLootItem(int itemId)
        {
            var item = GameItemData.Get(itemId);
            return item != null && !BattleItemRecipeData.IsRecipeOutputItem(itemId) &&
                   item.Type is ItemType.EQUIPMENT or ItemType.CONSUMABLE or ItemType.MATERIAL;
        }
    }
}
