using System.Collections.Concurrent;
using network.common;
using network.common.data;

namespace game_server.services;

public sealed class AreaItemStockManager
{
    private readonly ConcurrentDictionary<long, MatchingAreaItemStock> _matchingStocks = new();
    private readonly bool _naturalExploreLootEnabled;

    public AreaItemStockManager(bool naturalExploreLootEnabled = true)
    {
        _naturalExploreLootEnabled = naturalExploreLootEnabled;
    }

    public void InitializeMatching(long matchingId)
    {
        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        if (!_naturalExploreLootEnabled) return;

        lock (stock.SyncRoot)
        {
            foreach (var area in Enum.GetValues<AreaType>())
            {
                if (area == AreaType.None) continue;
                stock.GetOrCreateAreaStock((int)area);
            }
        }
    }

    public int GetRemainingCount(long matchingId, int areaType)
    {
        if (!_naturalExploreLootEnabled) return 0;

        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
            return stock.GetOrCreateAreaStock(areaType).Count;
    }

    /// <summary>
    /// 공개 미니맵용 상태다. 남은 개수는 서버에만 두고, 색상별 소진 여부까지만 반환한다.
    /// </summary>
    public IReadOnlyList<(AreaType AreaType, bool IsDepleted, List<OrbColor> AvailableOrbColors)>
        GetPublicDepletionSnapshot(long matchingId)
    {
        if (!_naturalExploreLootEnabled)
        {
            return Enum.GetValues<AreaType>()
                .Where(area => area != AreaType.None)
                .Select(area => (area, true, new List<OrbColor>()))
                .ToList();
        }

        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
        {
            return Enum.GetValues<AreaType>()
                .Where(area => area != AreaType.None)
                .Select(area =>
                {
                    var remainingItems = stock.GetOrCreateAreaStock((int)area);
                    var remainingColors = GetOrbColors(remainingItems);
                    return (
                        AreaType: area,
                        IsDepleted: remainingItems.Count == 0,
                        AvailableOrbColors: remainingColors.Order().ToList());
                })
                .ToList();
        }
    }

    private static HashSet<OrbColor> GetOrbColors(IEnumerable<int> itemIds)
    {
        var colors = new HashSet<OrbColor>();
        foreach (int itemId in itemIds)
            if (TryGetOrbMapColor(itemId, out var color))
                colors.Add(color);
        return colors;
    }

    private static bool TryGetOrbMapColor(int itemId, out OrbColor color)
    {
        if (OrbData.TryGetColorAndTier(itemId, out color, out _)) return true;
        if (OrbData.IsRecoveryOrb(itemId))
        {
            color = OrbColor.Recovery;
            return true;
        }

        color = OrbColor.None;
        return false;
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
                .Where(IsLootItem)
                .ToList();
            _areaStocks[areaType] = stock;
            return stock;
        }

        private static bool IsLootItem(int itemId)
        {
            var item = GameItemData.Get(itemId);
            return item != null && !BattleItemRecipeData.IsRecipeOutputItem(itemId) &&
                   item.Type is ItemType.EQUIPMENT or ItemType.CONSUMABLE or ItemType.MATERIAL;
        }
    }
}
