using network.common.data;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     플레이어 한 명이 보유한 오브 컬렉션. Player가 소유하며 읽기와 변경 모두 매치 잠금 안에서만 일어난다.
///     그래서 자체 동기화는 두지 않는다. 동일한 종류의 오브도 각각 독립 슬롯과 UID를 유지한다.
///     ItemUid는 프로세스 전역 순번이다.
/// </summary>
public class PlayerOrbCollection
{
    private static long s_nextItemUid;
    private readonly Dictionary<long, InGameItemInfo> _items = new();

    /// <summary>오브 하나를 독립 슬롯에 추가한다.</summary>
    public InGameItemInfo AddItem(int itemId)
    {
        if (GetOrbTier(itemId) <= 0)
            throw new ArgumentException("Only orb items can be stored.", nameof(itemId));
        var item = new InGameItemInfo
        {
            ItemUid = Interlocked.Increment(ref s_nextItemUid),
            ItemId = itemId,
            Count = 1
        };
        _items.Add(item.ItemUid, item);
        return item;
    }
    /// <summary>
    ///     오브 승급 시 ItemUid와 열 순번을 유지하고 ItemId만 바꾼다.
    ///     제거 후 추가하면 오브가 열 끝으로 이동하므로 기존 슬롯을 갱신한다.
    /// </summary>
    public bool TryReplaceOrb(long itemUid, int newItemId, out InGameItemInfo? replacedItem)
    {
        replacedItem = null;
        if (!_items.TryGetValue(itemUid, out var item) || item.Count <= 0)
            return false;
        if (!OrbData.IsOrbItem(newItemId))
            return false;

        item.ItemId = newItemId;
        replacedItem = item;
        return true;
    }

    /// <summary>오브 하나를 제거하고 Count가 0인 변경 결과를 반환한다.</summary>
    public bool TryRemoveItem(long itemUid, int count, out InGameItemInfo? updatedItem)
    {
        updatedItem = null;

        if (!_items.TryGetValue(itemUid, out var item)) return false;

        if (count != 1 || item.Count < count) return false;

        _items.Remove(itemUid);
        updatedItem = new InGameItemInfo
        {
            ItemUid = itemUid,
            ItemId = item.ItemId,
            Count = 0
        };

        return true;
    }

    public bool TryAddItemWithCapacity(int itemId, int maxSlots, out InGameItemInfo? addedItem)
    {
        addedItem = null;
        if (GetOrbTier(itemId) <= 0 || maxSlots <= 0 || _items.Count >= maxSlots) return false;
        addedItem = AddItem(itemId);

        return true;
    }

    /// <summary>전부 꺼내고 비운다. 탈락 드롭이 쓴다.</summary>
    public List<InGameItemInfo> TakeAllItems()
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

    /// <summary>오브 티어별 가중치와 수량을 합산한다. 봇의 추격과 성장 판단이 같은 전력을 사용한다.</summary>
    internal float GetOrbPower()
    {
        float power = 0f;
        foreach (var item in GetAllItems())
        {
            if (item.Count <= 0) continue;
            int tier = GetOrbTier(item.ItemId);
            if (tier <= 0) continue;
            power += OrbData.GetSwarmStatTierWeight(tier) * item.Count;
        }
        return power;
    }

    /// <summary>보유 오브를 ItemUid 순으로 반환한다. 강화 대상과 월드 꼬리 순번이 이 순서를 공유한다.</summary>
    internal List<InGameItemInfo> GetOrderedOrbs() =>
        GetAllItems()
            .Where(item => item.Count > 0 && GetOrbTier(item.ItemId) > 0)
            .OrderBy(item => item.ItemUid)
            .ToList();

    /// <summary>수량이 남은 공격·회복 오브가 하나라도 있는지 확인한다.</summary>
    public bool HasAnyOrb() =>
        GetAllItems().Any(item => item.Count > 0 && GetOrbTier(item.ItemId) > 0);

    /// <summary>보유한 공격·회복 오브의 수량과 티어 합계.</summary>
    public (int OrbCount, int TierSum) GetOrbScore()
    {
        int orbCount = 0;
        int tierSum = 0;
        foreach (var item in GetAllItems())
        {
            int tier = GetOrbTier(item.ItemId);
            if (item.Count <= 0 || tier <= 0)
            {
                continue;
            }
            orbCount += item.Count;
            tierSum += tier * item.Count;
        }
        return (orbCount, tierSum);
    }

    /// <summary>현재 보유 오브 중 최고 티어. 오브가 없으면 0.</summary>
    public int GetHighestOrbTier() =>
        GetOrderedOrbs().Select(item => GetOrbTier(item.ItemId)).DefaultIfEmpty(0).Max();

    /// <summary>보유 오브 목록.</summary>
    public List<InGameItemInfo> GetAllItems()
    {
        return _items.Values.ToList();
    }

    internal static int GetOrbTier(int itemId) =>
        OrbData.TryGetColorAndTier(itemId, out _, out int attackTier)
            ? attackTier
            : OrbData.TryGetRecoveryTier(itemId, out int recoveryTier) ? recoveryTier : 0;
}
