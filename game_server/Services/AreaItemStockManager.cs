using System.Collections.Concurrent;
using network.common;
using network.common.data;

namespace game_server.services;

public sealed class AreaItemStockManager
{
    private readonly ConcurrentDictionary<long, MatchingAreaItemStock> _matchingStocks = new();
    private readonly Random _random;

    public AreaItemStockManager(Random? random = null)
    {
        _random = random ?? Random.Shared;
    }

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
        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
        {
            if (!stock.AppliedSupplyWaves.Add(closureAtUnixMs)) return [];

            var unavailable = warningAreas.Concat(closedAreas).ToHashSet();
            var candidates = Enum.GetValues<AreaType>()
                .Where(area => area != AreaType.None && !area.IsCorridor() && !unavailable.Contains(area))
                .Where(area => GameInteractableData.GetItemPoolByArea((int)area).Count > 0)
                .OrderBy(area => stock.GetOrCreateAreaStock((int)area).Count)
                .ThenBy(_ => _random.Next())
                .Take(SupplyItemIds.Length)
                .ToArray();

            if (candidates.Length < SupplyItemIds.Length) return [];

            var supply = SupplyItemIds.OrderBy(_ => _random.Next()).ToArray();
            var added = new List<(AreaType AreaType, int ItemId)>(SupplyItemIds.Length);
            for (int index = 0; index < candidates.Length; index++)
            {
                int itemId = supply[index];
                stock.GetOrCreateAreaStock((int)candidates[index]).Add(itemId);
                added.Add((candidates[index], itemId));
            }

            return added;
        }
    }

    /// <summary>
    /// Server-only routing query for bots. The minimap intentionally exposes only depletion,
    /// while bots need to know whether a route can still rebuild their active orb resonance.
    /// </summary>
    public bool HasRemainingOrbColor(long matchingId, int areaType, SurvivorOrbColor color)
    {
        if (color == SurvivorOrbColor.None) return false;

        var stock = _matchingStocks.GetOrAdd(matchingId, _ => new MatchingAreaItemStock());
        lock (stock.SyncRoot)
            return stock.GetOrCreateAreaStock(areaType)
                .Any(itemId => SurvivorOrbData.TryGetColorAndTier(itemId, out var itemColor, out _) && itemColor == color);
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

    /// <summary>
    /// 공개 미니맵용 상태다. 남은 개수는 서버에만 두고, 색상별 소진 여부까지만 반환한다.
    /// </summary>
    public IReadOnlyList<(AreaType AreaType, bool IsDepleted, List<SurvivorOrbColor> AvailableOrbColors)>
        GetPublicDepletionSnapshot(long matchingId)
    {
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

    private static HashSet<SurvivorOrbColor> GetOrbColors(IEnumerable<int> itemIds)
    {
        var colors = new HashSet<SurvivorOrbColor>();
        foreach (int itemId in itemIds)
            if (SurvivorOrbData.TryGetColorAndTier(itemId, out var color, out _))
                colors.Add(color);
        return colors;
    }

    public void RemoveMatchingState(long matchingId) => _matchingStocks.TryRemove(matchingId, out _);

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
