using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     플레이어 한 명의 보유 오브·소환석·강화 횟수·궤적·공격 타이머 상태.
///     Player가 소유하며 읽기와 변경 모두 매치 잠금 안에서만 일어난다.
///     그래서 자체 동기화는 두지 않는다. 동일한 종류의 오브도 각각 독립 슬롯과 UID를 유지한다.
///     ItemUid는 프로세스 전역 순번이다.
/// </summary>
public class PlayerOrbState
{
    private static long _nextItemUid;
    private readonly Dictionary<long, InGameItemInfo> _items = new();
    private readonly Dictionary<int, int> _upgradeCounts = new();
    public SummonStoneStateInfo SummonStones { get; internal set; } = SummonStoneStateInfo.Empty;
    private readonly Dictionary<long, DateTime> _nextAttackAtUtc = new();
    internal List<Vector3f> OrbTrail { get; } = new();

    public int GetUpgradeCount(int orbGroupId) => _upgradeCounts.GetValueOrDefault(orbGroupId);

    public int IncrementUpgradeCount(int orbGroupId)
    {
        int count = GetUpgradeCount(orbGroupId) + 1;
        _upgradeCounts[orbGroupId] = count;
        return count;
    }

    internal bool IsOrbAttackReady(long itemUid, DateTime nowUtc)
    {
        double intervalSeconds = GetAttackIntervalSeconds(itemUid);
        if (intervalSeconds <= 0d)
        {
            return false;
        }
        // 첫 발동은 시작 시간만 심고 false로 돌려서 일제 발동 방지
        if (!_nextAttackAtUtc.TryGetValue(itemUid, out var nextAttackAtUtc))
        {
            double firstPhase = 0.5d + itemUid % 977 / 977d; // 0.5~1.5배
            _nextAttackAtUtc[itemUid] = nowUtc.AddSeconds(intervalSeconds * firstPhase);
            return false;
        }
        return nowUtc >= nextAttackAtUtc;
    }

    internal void ScheduleNextOrbAttack(long itemUid, DateTime nowUtc) =>
        _nextAttackAtUtc[itemUid] = nowUtc.AddSeconds(GetAttackIntervalSeconds(itemUid));

    internal double GetAttackIntervalSeconds(long itemUid)
    {
        if (!_items.TryGetValue(itemUid, out var item) || !OrbData.TryGetOrbGroupAndTier(item.ItemId, out int orbGroupId, out _))
        {
            return 0d;
        }

        switch (orbGroupId)
        {
            case OrbGroupIds.Wind:
                return Config.SWARM_WIND_BLADE_TICK_SECONDS;
            case OrbGroupIds.Wave:
                return Config.SWARM_WAVE_VORTEX_INTERVAL_SECONDS;
            case OrbGroupIds.Sun:
                return Config.SWARM_CROSSFIRE_SUN_INTERVAL_SECONDS;
            default:
                return 0d;
        }
    }

    internal DateTime? GetNextOrbAttackAtUtc(long itemUid) =>
        _nextAttackAtUtc.TryGetValue(itemUid, out var nextAttackAtUtc) ? nextAttackAtUtc : null;

    internal void RemoveAttackTimers(long itemUid)
    {
        _nextAttackAtUtc.Remove(itemUid);
    }

    internal void ClearAttackTimers()
    {
        _nextAttackAtUtc.Clear();
    }

    public InGameItemInfo AddOrb(int itemId)
    {
        if (GetOrbTier(itemId) <= 0)
        {
            throw new ArgumentException("Only orb items can be stored.", nameof(itemId));
        }
        var item = new InGameItemInfo
        {
            ItemUid = Interlocked.Increment(ref _nextItemUid),
            ItemId = itemId,
            Count = 1
        };
        _items.Add(item.ItemUid, item);
        return item;
    }

    public bool TryReplaceOrb(long itemUid, int newItemId, out InGameItemInfo? replacedItem)
    {
        replacedItem = null;
        if (!_items.TryGetValue(itemUid, out var item) || item.Count <= 0)
        {
            return false;
        }
        if (!OrbData.IsOrbItem(newItemId))
        {
            return false;
        }

        item.ItemId = newItemId;
        replacedItem = item;
        return true;
    }

    public bool TryRemoveOrb(long itemUid, int count, out InGameItemInfo? updatedItem)
    {
        updatedItem = null;

        if (!_items.TryGetValue(itemUid, out var item))
        {
            return false;
        }
        if (count != 1 || item.Count < count)
        {
            return false;
        }
        _items.Remove(itemUid);
        updatedItem = new InGameItemInfo
        {
            ItemUid = itemUid,
            ItemId = item.ItemId,
            Count = 0
        };

        return true;
    }

    public bool TryAddOrbWithCapacity(int itemId, int maxSlots, out InGameItemInfo? addedItem)
    {
        addedItem = null;
        if (GetOrbTier(itemId) <= 0 || maxSlots <= 0 || _items.Count >= maxSlots) return false;
        addedItem = AddOrb(itemId);
        return true;
    }

    public List<InGameItemInfo> TakeAllOrbs()
    {
        var items = _items.Values.Select(item => new InGameItemInfo
        {
            ItemUid = item.ItemUid,
            ItemId = item.ItemId,
            Count = item.Count,
            GiftState = item.GiftState
        }).ToList();
        _items.Clear();
        return items;
    }

    public int OrbCount => _items.Count;

    internal float GetOrbPower()
    {
        float power = 0f;
        foreach (var item in _items.Values)
        {
            power += BattleItemCombatData.GetStatTierWeight(GetOrbTier(item.ItemId));
        }
        return power;
    }

    internal List<InGameItemInfo> GetOrderedOrbs() => _items.Values.OrderBy(item => item.ItemUid).ToList();

    public bool HasAnyOrb() => _items.Count > 0;

    public (int OrbCount, int TierSum) GetOrbScore()
    {
        int tierSum = 0;
        foreach (var item in _items.Values)
        {
            tierSum += GetOrbTier(item.ItemId);
        }
        return (_items.Count, tierSum);
    }

    public int GetHighestOrbTier() => GetOrderedOrbs().Select(item => GetOrbTier(item.ItemId)).DefaultIfEmpty(0).Max();

    public List<InGameItemInfo> GetAllOrbs()
    {
        return _items.Values.ToList();
    }

    internal static int GetOrbTier(int itemId) => OrbData.TryGetOrbGroupAndTier(itemId, out _, out int attackTier) ? attackTier : 0;
}
