using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     단일 플레이어의 게임 내 배낭
/// </summary>
public class PlayerInGameInventory(long matchingId)
{
    private readonly ConcurrentDictionary<long, InGameItemInfo> _items = new();
    private readonly object _sequenceLock = new();
    private int _nextSequence = 1;

    private long _equippedBattleItemUid;
    private static bool ShouldKeepSeparateStack(int itemId, GiftState giftState) =>
        giftState == GiftState.None &&
        (OrbData.IsOrbItem(itemId) || BattleItemRecipeData.IsRecipeInputItem(itemId));

    /// <summary>
    ///     ItemUid 생성: MatchingId * 100000 + Sequence
    /// </summary>
    private long GenerateUid()
    {
        lock (_sequenceLock)
        {
            return matchingId * 100000 + _nextSequence++;
        }
    }

    /// <summary>
    ///     아이템 추가. 스택 가능하면 기존 아이템에 수량 추가, 아니면 새로 생성
    /// </summary>
    /// <returns>변경된 아이템 정보</returns>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public InGameItemInfo AddItem(int itemId, int count = 1, GiftState giftState = GiftState.None,
        bool forceSeparateStack = false)
    {
        bool keepSeparateStack = forceSeparateStack || ShouldKeepSeparateStack(itemId, giftState);

        // 오브 열의 개별 UID와 조합 재료 선택을 위해 각각 독립된 슬롯을 유지한다.
        if (!keepSeparateStack)
        {
            foreach (var kvp in _items)
                if (kvp.Value.ItemId == itemId && kvp.Value.GiftState == giftState)
                {
                    kvp.Value.Count += count;
                    return kvp.Value;
                }
        }

        var newItem = new InGameItemInfo { ItemUid = GenerateUid(), ItemId = itemId, Count = count, GiftState = giftState };
        _items[newItem.ItemUid] = newItem;
        return newItem;
    }

    /// <summary>
    ///     오브 승급 시 ItemUid와 열 순번을 유지하고 ItemId만 바꾼다.
    ///     제거 후 추가하면 오브가 열 끝으로 이동하므로 기존 슬롯을 갱신한다.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
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

    /// <summary>
    ///     아이템 사용/제거
    /// </summary>
    /// <returns>성공 여부와 변경된 아이템 정보</returns>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryRemoveItem(long itemUid, int count, out InGameItemInfo? updatedItem)
    {
        updatedItem = null;

        if (!_items.TryGetValue(itemUid, out var item)) return false;

        if (item.Count < count) return false;

        item.Count -= count;

        if (item.Count <= 0)
        {
            _items.TryRemove(itemUid, out _);
            if (_equippedBattleItemUid == itemUid)
                _equippedBattleItemUid = 0;
            updatedItem = new InGameItemInfo
            {
                ItemUid = itemUid,
                ItemId = item.ItemId,
                GiftState = item.GiftState,
                Count = 0 // 삭제됨을 표시
            };
        }
        else
        {
            updatedItem = item;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryAddItemWithCapacity(int itemId, int maxSlots, out InGameItemInfo? addedItem,
        GiftState giftState = GiftState.None)
    {
        addedItem = null;
        if (maxSlots <= 0 || _items.Count >= maxSlots) return false;
        addedItem = AddItem(itemId, 1, giftState, forceSeparateStack: true);
        if (_equippedBattleItemUid == 0 && OrbData.IsOrbItem(itemId))
            _equippedBattleItemUid = addedItem.ItemUid;
        return true;
    }
    /// <summary>
    ///     특정 아이템 조회
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryRemoveOneByItemId(int itemId, out InGameItemInfo? updatedItem)
    {
        updatedItem = null;
        var item = _items.Values.FirstOrDefault(candidate => candidate.ItemId == itemId && candidate.Count > 0);
        return item != null && TryRemoveItem(item.ItemUid, 1, out updatedItem);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryCombineItems(IReadOnlyCollection<int> inputItemIds, int outputItemId,
        out List<InGameItemInfo> changedItems)
    {
        changedItems = new List<InGameItemInfo>();
        if (inputItemIds.Count < 2 || outputItemId <= 0) return false;

        bool replaceEquippedBattleItem = _equippedBattleItemUid != 0 &&
                                          _items.TryGetValue(_equippedBattleItemUid, out var equippedItem) &&
                                          inputItemIds.Contains(equippedItem.ItemId);

        if (!HasItems(inputItemIds))
            return false;

        foreach (int inputItemId in inputItemIds)
        {
            if (!TryRemoveOneByItemId(inputItemId, out var removed) || removed == null)
                throw new InvalidOperationException("Inventory changed during atomic battle item combine.");
            changedItems.Add(removed);
        }

        var outputItem = AddItem(outputItemId, 1, forceSeparateStack: true);
        changedItems.Add(outputItem);
        if (replaceEquippedBattleItem && BattleItemCombatData.IsCombatItem(outputItemId))
            _equippedBattleItemUid = outputItem.ItemUid;
        return true;
    }


    /// <summary>
    ///     Checks a matching recipe's materials, draws its outcome, and consumes the inputs under
    ///     one inventory monitor so a rejected combine cannot advance the supplied random stream.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryCombineRandomRecipe(
        IReadOnlyList<BattleItemRecipe> candidates,
        Random random,
        out BattleItemRecipe? selectedRecipe,
        out List<InGameItemInfo> changedItems)
    {
        ArgumentNullException.ThrowIfNull(random);
        selectedRecipe = null;
        changedItems = new List<InGameItemInfo>();
        if (candidates.Count == 0)
            return false;

        int[] expectedInputs = candidates[0].InputItemIds.OrderBy(itemId => itemId).ToArray();
        if (candidates.Skip(1).Any(candidate =>
                !candidate.InputItemIds.OrderBy(itemId => itemId).SequenceEqual(expectedInputs)))
        {
            throw new ArgumentException(
                "Random recipe candidates must describe the same input multiset.",
                nameof(candidates));
        }

        if (!HasItems(candidates[0].InputItemIds))
            return false;

        selectedRecipe = candidates[random.Next(candidates.Count)];
        if (!TryCombineItems(
                selectedRecipe.InputItemIds,
                selectedRecipe.OutputItemId,
                out changedItems))
        {
            throw new InvalidOperationException(
                "Inventory changed during atomic random recipe combine.");
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
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
        _equippedBattleItemUid = 0;
        return items;
    }
    [MethodImpl(MethodImplOptions.Synchronized)]
    public InGameItemInfo? GetItem(long itemUid)
    {
        return _items.GetValueOrDefault(itemUid);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryEquipBattleItem(long itemUid, out InGameItemInfo? equippedItem)
    {
        equippedItem = null;
        if (!_items.TryGetValue(itemUid, out var item) || item.Count <= 0 ||
            !BattleItemCombatData.IsCombatItem(item.ItemId))
        {
            return false;
        }

        _equippedBattleItemUid = itemUid;
        equippedItem = item;
        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public InGameItemInfo? GetEquippedBattleItem()
    {
        if (_equippedBattleItemUid != 0 &&
            _items.TryGetValue(_equippedBattleItemUid, out var item) && item.Count > 0 &&
            BattleItemCombatData.IsCombatItem(item.ItemId))
        {
            return item;
        }

        _equippedBattleItemUid = 0;
        return null;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryGetActiveOrbPair(out OrbColor color, out int pairTier)
    {
        var boardItemIds = _items.Values
            .Where(item => item.Count > 0)
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count));
        return OrbData.TryGetActivePair(boardItemIds, out color, out pairTier);
    }

    /// <summary>오브 티어별 가중치와 수량을 합산한다. 봇의 추격과 성장 판단이 같은 전력을 사용한다.</summary>
    internal float GetOrbPower()
    {
        float power = 0f;
        foreach (var item in GetAllItems())
        {
            if (item.Count <= 0) continue;
            int tier = OrbData.TryGetColorAndTier(item.ItemId, out _, out int attackTier)
                ? attackTier
                : OrbData.TryGetRecoveryTier(item.ItemId, out int recoveryTier) ? recoveryTier : 0;
            if (tier <= 0) continue;
            power += OrbData.GetSwarmStatTierWeight(tier) * item.Count;
        }
        return power;
    }

    /// <summary>보유 오브를 ItemUid 순으로 반환한다. 강화 대상과 월드 꼬리 순번이 이 순서를 공유한다.</summary>
    internal List<InGameItemInfo> GetOrderedOrbs() =>
        GetAllItems()
            .Where(item => item.Count > 0 &&
                (OrbData.TryGetColorAndTier(item.ItemId, out _, out int tier)
                    ? tier > 0
                    : OrbData.TryGetRecoveryTier(item.ItemId, out int recoveryTier) && recoveryTier > 0))
            .OrderBy(item => item.ItemUid)
            .ToList();

    /// <summary>전체 아이템 목록.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<InGameItemInfo> GetAllItems()
    {
        return _items.Values.ToList();
    }

    /// <summary>
    ///     특정 종류의 아이템 수량 합계
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int GetItemCount(int itemId)
    {
        return _items.Values.Where(i => i.ItemId == itemId).Sum(i => i.Count);
    }

    /// <summary>Checks duplicate-aware material quantities without mutating the inventory.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool HasItems(IReadOnlyCollection<int> itemIds)
    {
        if (itemIds.Count == 0)
            return false;

        return itemIds
            .GroupBy(itemId => itemId)
            .All(required => GetItemCount(required.Key) >= required.Count());
    }
}

/// <summary>
///     단일 매칭 인스턴스의 모든 플레이어 배낭
/// </summary>
public class MatchingInventoryState(long matchingId)
{
    private readonly ConcurrentDictionary<long, PlayerInGameInventory> _playerInventories = new();

    /// <summary>
    ///     플레이어 인벤토리 가져오기 (없으면 생성)
    /// </summary>
    public PlayerInGameInventory GetOrCreatePlayerInventory(long playerId)
    {
        return _playerInventories.GetOrAdd(playerId, _ => new PlayerInGameInventory(matchingId));
    }

}

/// <summary>
///     매치 하나의 플레이어 인벤토리를 보관하고 아이템 추가·소비·조합을 처리한다.
///     MatchRuntime마다 별도 객체를 만들며, 매치 종료 후에는 인벤토리를 다시 만들지 않는다.
/// </summary>
public class InGameInventoryManager
{
    private readonly long _matchingId;
    private MatchingInventoryState? _state;
    private readonly Action<string>? _logAction;

    internal InGameInventoryManager(long matchingId, Action<string>? logAction = null)
    {
        _matchingId = matchingId;
        _state = new MatchingInventoryState(matchingId);
        _logAction = logAction;
    }

    internal void Release() => Interlocked.Exchange(ref _state, null);

    /// <summary>
    ///     이미 생성된 매치의 인벤토리 상태를 조회한다.
    /// </summary>
    private MatchingInventoryState GetMatchingState() =>
        Volatile.Read(ref _state)
        ?? throw new InvalidOperationException($"Match is not available: {_matchingId}");

    /// <summary>
    ///     플레이어 인벤토리 가져오기
    /// </summary>
    public PlayerInGameInventory GetPlayerInventory(long playerId)
    {
        var matchingState = GetMatchingState();
        return matchingState.GetOrCreatePlayerInventory(playerId);
    }

    /// <summary>
    ///     아이템 추가
    /// </summary>
    public InGameItemInfo AddItem(long playerId, int itemId, int count = 1,
        GiftState giftState = GiftState.None)
    {
        var inventory = GetPlayerInventory(playerId);
        var item = inventory.AddItem(itemId, count, giftState);
        _logAction?.Invoke(
            $"InGameInventoryManager: Added item (MatchingId={_matchingId}, PlayerId={playerId}, ItemId={itemId}, Count={count}, GiftState={giftState}, ItemUid={item.ItemUid})");
        return item;
    }

    /// <summary>
    ///     아이템 사용/제거
    /// </summary>
    public bool TryRemoveItem(long playerId, long itemUid, int count, out InGameItemInfo? updatedItem)
    {
        var inventory = GetPlayerInventory(playerId);
        bool result = inventory.TryRemoveItem(itemUid, count, out updatedItem);
        if (result)
            _logAction?.Invoke(
                $"InGameInventoryManager: Removed item (MatchingId={_matchingId}, PlayerId={playerId}, ItemUid={itemUid}, Count={count})");
        return result;
    }

    public bool TryAddItemWithCapacity(long playerId, int itemId, int maxSlots,
        out InGameItemInfo? addedItem, GiftState giftState = GiftState.None)
    {
        var inventory = GetPlayerInventory(playerId);
        bool result = inventory.TryAddItemWithCapacity(itemId, maxSlots, out addedItem, giftState);
        if (result && addedItem != null)
            _logAction?.Invoke($"InGameInventoryManager: Added capacity-limited item (MatchingId={_matchingId}, PlayerId={playerId}, ItemId={itemId}, ItemUid={addedItem.ItemUid}, MaxSlots={maxSlots})");
        return result;
    }
    /// <summary>
    ///     ItemId로 아이템 제거 (탈출 아이템 전달용)
    /// </summary>
    public bool TryRemoveOneByItemId(long playerId, int itemId, out InGameItemInfo? updatedItem)
    {
        var inventory = GetPlayerInventory(playerId);
        bool result = inventory.TryRemoveOneByItemId(itemId, out updatedItem);
        if (result)
            _logAction?.Invoke(
                $"InGameInventoryManager: Removed one item by ItemId (MatchingId={_matchingId}, PlayerId={playerId}, ItemId={itemId}, ItemUid={updatedItem?.ItemUid})");
        return result;
    }

    public bool TryCombineItems(long playerId, IReadOnlyCollection<int> inputItemIds,
        int outputItemId, out List<InGameItemInfo> changedItems)
    {
        var inventory = GetPlayerInventory(playerId);
        bool result = inventory.TryCombineItems(inputItemIds, outputItemId, out changedItems);
        if (result)
            _logAction?.Invoke(
                $"InGameInventoryManager: Combined items (MatchingId={_matchingId}, PlayerId={playerId}, Inputs=[{string.Join(',', inputItemIds)}], Output={outputItemId})");
        return result;
    }


    public bool TryCombineRandomRecipe(
        long playerId,
        IReadOnlyList<BattleItemRecipe> candidates,
        Random random,
        out BattleItemRecipe? selectedRecipe,
        out List<InGameItemInfo> changedItems)
    {
        var inventory = GetPlayerInventory(playerId);
        bool result = inventory.TryCombineRandomRecipe(
            candidates,
            random,
            out selectedRecipe,
            out changedItems);
        if (result)
            _logAction?.Invoke(
                $"InGameInventoryManager: Combined items (MatchingId={_matchingId}, PlayerId={playerId}, Inputs=[{string.Join(',', selectedRecipe!.InputItemIds)}], Output={selectedRecipe.OutputItemId})");
        return result;
    }

    public bool TryEquipBattleItem(long playerId, long itemUid,
        out InGameItemInfo? equippedItem)
    {
        var inventory = GetPlayerInventory(playerId);
        bool result = inventory.TryEquipBattleItem(itemUid, out equippedItem);
        if (result)
        {
            _logAction?.Invoke(
                $"InGameInventoryManager: Equipped battle item (MatchingId={_matchingId}, PlayerId={playerId}, ItemId={equippedItem?.ItemId}, ItemUid={itemUid})");
        }

        return result;
    }

    public InGameItemInfo? GetEquippedBattleItem(long playerId)
    {
        return GetPlayerInventory(playerId).GetEquippedBattleItem();
    }

    /// <summary>장착 배틀아이템의 티어 (미장착 0) — 매치 정산의 최종 오브 티어 산정 공용 경로 (#297 중복 단일화).</summary>
    public int GetEquippedBattleItemTier(long playerId)
    {
        var equippedItem = GetEquippedBattleItem(playerId);
        return equippedItem == null ? 0 : BattleItemCombatData.Get(equippedItem.ItemId)?.Tier ?? 0;
    }


    /// <summary>
    ///     플레이어의 전체 아이템 목록
    /// </summary>
    public List<InGameItemInfo> GetAllItems(long playerId)
    {
        var inventory = GetPlayerInventory(playerId);
        return inventory.GetAllItems();
    }

    /// <summary>
    ///     매칭 종료 시 해당 매칭의 상태 정리
    /// </summary>
    public List<InGameItemInfo> TakeAllItems(long playerId)
    {
        var inventory = GetPlayerInventory(playerId);
        var items = inventory.TakeAllItems();
        _logAction?.Invoke(
            $"InGameInventoryManager: Dropped all items (MatchingId={_matchingId}, PlayerId={playerId}, Slots={items.Count})");
        return items;
    }
}
