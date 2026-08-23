using System.Collections.Concurrent;
using network.common;
using network.common.data;

namespace game_server.services;

public sealed class AreaItemStockManager
{
    private readonly ConcurrentDictionary<long, MatchingAreaItemStock> _matchingStocks = new();
    private readonly Random _random;
    private readonly bool _naturalExploreLootEnabled;

    public AreaItemStockManager(Random? random = null, bool naturalExploreLootEnabled = true)
    {
        _random = random ?? Random.Shared;
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

    public bool HasRemaining(long matchingId, int areaType)
    {
        if (!_naturalExploreLootEnabled) return false;

        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
            return stock.GetOrCreateAreaStock(areaType).Count > 0;
    }

    public bool TryConsumeDrops(long matchingId, int areaType, int maxCount, out List<int> itemIds)
    {
        itemIds = new List<int>();
        if (!_naturalExploreLootEnabled || maxCount <= 0) return false;

        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
        {
            var areaStock = stock.GetOrCreateAreaStock(areaType);
            int count = Math.Min(maxCount, areaStock.Count);
            for (int i = 0; i < count; i++)
            {
                int index = _random.Next(areaStock.Count);
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
        if (!_naturalExploreLootEnabled) return 0;

        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
            return stock.GetOrCreateAreaStock(areaType).Count;
    }

    /// <summary>
    /// Adds one of each P0 orb type to distinct safe regions when a closure warning begins.
    /// The final convergence warning is intentionally skipped when fewer than four safe regions remain.
    /// </summary>
    public IReadOnlyList<(AreaType AreaType, int ItemId)> ReplenishForClosureWarning(
        long matchingId,
        long closureAtUnixMs,
        IReadOnlyCollection<AreaType> warningAreas,
        IReadOnlyCollection<AreaType> closedAreas)
    {
        if (!_naturalExploreLootEnabled) return [];

        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
        {
            if (stock.AppliedSupplyWaves.Contains(closureAtUnixMs)) return [];

            var unavailable = warningAreas.Concat(closedAreas).ToHashSet();
            var candidates = Enum.GetValues<AreaType>()
                .Where(area => area != AreaType.None && !area.IsCorridor() && !unavailable.Contains(area))
                .Where(area => GameInteractableData.GetItemPoolByArea((int)area).Count > 0)
                .ToArray();

            var supply = SupplyItemIds.OrderBy(_ => _random.Next()).ToArray();
            var planned = new List<(AreaType AreaType, int ItemId)>(SupplyItemIds.Length);
            if (!TryPlanSupply(0)) return [];

            stock.AppliedSupplyWaves.Add(closureAtUnixMs);
            foreach (var entry in planned)
                stock.GetOrCreateAreaStock((int)entry.AreaType).Add(entry.ItemId);

            return planned;

            bool TryPlanSupply(int supplyIndex)
            {
                if (supplyIndex >= supply.Length) return true;

                int itemId = supply[supplyIndex];
                foreach (var area in candidates
                             .Where(area => planned.All(entry => entry.AreaType != area))
                             .Where(area => CanAddOrbType(stock.GetOrCreateAreaStock((int)area), itemId))
                             .OrderBy(area => stock.GetOrCreateAreaStock((int)area).Count)
                             .ThenBy(_ => _random.Next()))
                {
                    planned.Add((area, itemId));
                    if (TryPlanSupply(supplyIndex + 1)) return true;
                    planned.RemoveAt(planned.Count - 1);
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Server-only routing query for bots. The minimap intentionally exposes only depletion,
    /// while bots need to know whether a route can still rebuild their active orb resonance.
    /// </summary>
    public bool HasRemainingOrbColor(long matchingId, int areaType, OrbColor color)
    {
        if (!_naturalExploreLootEnabled || color == OrbColor.None) return false;

        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
            return stock.GetOrCreateAreaStock(areaType)
                .Any(itemId => TryGetOrbMapColor(itemId, out var itemColor) && itemColor == color);
    }

    public IReadOnlyDictionary<int, int> GetRemainingSnapshot(long matchingId, int areaType)
    {
        if (!_naturalExploreLootEnabled) return new Dictionary<int, int>();

        if (!_matchingStocks.TryGetValue(matchingId, out var stock))
            return new Dictionary<int, int>();

        lock (stock.SyncRoot)
        {
            return stock.GetOrCreateAreaStock(areaType)
                .GroupBy(id => id)
                .ToDictionary(group => group.Key, group => group.Count());
        }
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

    private static bool CanAddOrbType(IEnumerable<int> itemIds, int addedItemId)
    {
        var colors = GetOrbColors(itemIds);
        if (!TryGetOrbMapColor(addedItemId, out var addedColor)) return true;
        colors.Add(addedColor);
        return colors.Count <= MaxOrbTypesPerArea;
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

    private const int MaxOrbTypesPerArea = 3;
    private static readonly int[] SupplyItemIds = [107000010, 107000020, 107000030, 107000040];

    private sealed class MatchingAreaItemStock
    {
        private readonly Dictionary<int, List<int>> _areaStocks = new();
        public HashSet<long> AppliedSupplyWaves { get; } = new();
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
