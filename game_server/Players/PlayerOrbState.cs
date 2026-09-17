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

    // 오브별 공격 주기·준비 상태
    private readonly Dictionary<long, OrbAttackTimerState> _attackTimers = new();

    // 이동 궤적
    internal List<Vector3f> OrbTrail { get; } = new();

    private sealed class OrbAttackTimerState
    {
        public DateTime? NextAttackAtUtc { get; set; }
        public DateTime? EngagementStartedAtUtc { get; set; }
    }

    private OrbAttackTimerState GetOrCreateAttackTimers(long itemUid)
    {
        if (_attackTimers.TryGetValue(itemUid, out var timers))
        {
            return timers;
        }
        timers = new OrbAttackTimerState();
        _attackTimers.Add(itemUid, timers);
        return timers;
    }

    public int GetUpgradeCount(int orbGroupId) => _upgradeCounts.GetValueOrDefault(orbGroupId);

    public int IncrementUpgradeCount(int orbGroupId)
    {
        int count = GetUpgradeCount(orbGroupId) + 1;
        _upgradeCounts[orbGroupId] = count;
        return count;
    }

    internal bool TryBeginWindOrbAttack(long itemUid, DateTime nowUtc, double intervalSeconds)
    {
        var timers = GetOrCreateAttackTimers(itemUid);
        if (timers.NextAttackAtUtc.HasValue && nowUtc < timers.NextAttackAtUtc.Value)
        {
            return false;
        }

        timers.NextAttackAtUtc = nowUtc.AddSeconds(intervalSeconds);
        return true;
    }

    internal void ResetWindOrbEngagement(long itemUid)
    {
        if (_attackTimers.TryGetValue(itemUid, out var timers))
        {
            timers.EngagementStartedAtUtc = null;
        }
    }

    internal bool UpdateWindOrbSpinup(long itemUid, DateTime nowUtc, double durationSeconds)
    {
        var timers = GetOrCreateAttackTimers(itemUid);
        timers.EngagementStartedAtUtc ??= nowUtc;
        return (nowUtc - timers.EngagementStartedAtUtc.Value).TotalSeconds >= durationSeconds;
    }

    internal bool UpdateWaveOrbAttackReadiness(long itemUid, DateTime nowUtc, double intervalSeconds, double firstPhase)
    {
        var timers = GetOrCreateAttackTimers(itemUid);
        if (!timers.NextAttackAtUtc.HasValue)
        {
            timers.NextAttackAtUtc = nowUtc.AddSeconds(intervalSeconds * firstPhase);
            return false;
        }
        return nowUtc >= timers.NextAttackAtUtc.Value;
    }

    internal void ScheduleNextWaveOrbAttack(long itemUid, DateTime nowUtc, double intervalSeconds) =>
        GetOrCreateAttackTimers(itemUid).NextAttackAtUtc = nowUtc.AddSeconds(intervalSeconds);

    internal DateTime? GetNextWaveOrbAttackAtUtc(long itemUid) =>
        _attackTimers.TryGetValue(itemUid, out var timers) ? timers.NextAttackAtUtc : null;

    internal void RemoveAttackTimers(long itemUid)
    {
        _attackTimers.Remove(itemUid);
    }

    internal void ClearAttackTimers()
    {
        _attackTimers.Clear();
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
            power += OrbData.GetSwarmStatTierWeight(GetOrbTier(item.ItemId));
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

    internal static int GetOrbTier(int itemId) => OrbData.TryGetColorAndTier(itemId, out _, out int attackTier) ? attackTier : 0;
}
