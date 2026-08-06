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
        giftState == GiftState.None && BattleItemRecipeData.IsRecipeInputItem(itemId);

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

        // Merge puzzle inputs need separate slots so duplicate materials can be selected independently.
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
        if (_equippedBattleItemUid == 0 && SurvivorOrbData.IsSurvivorOrb(itemId))
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

        var requiredCounts = inputItemIds
            .GroupBy(itemId => itemId)
            .ToDictionary(group => group.Key, group => group.Count());
        if (requiredCounts.Any(required => GetItemCount(required.Key) < required.Value))
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

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryCombineSurvivorOrbs(int inputA, int inputB, Random random,
        out int outputItemId, out List<InGameItemInfo> changedItems)
    {
        outputItemId = 0;
        changedItems = new List<InGameItemInfo>();
        if (!SurvivorOrbData.TryGetRandomMergeOutput(inputA, inputB, random, out outputItemId))
            return false;

        return TryCombineItems([inputA, inputB], outputItemId, out changedItems);
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
    public bool TryGetActiveSurvivorOrbPair(out SurvivorOrbColor color, out int pairTier)
    {
        var boardItemIds = _items.Values
            .Where(item => item.Count > 0)
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count));
        return SurvivorOrbData.TryGetActivePair(boardItemIds, out color, out pairTier);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool HasActiveSurvivorOrbPair(SurvivorOrbColor color, out int pairTier)
    {
        var boardItemIds = _items.Values
            .Where(item => item.Count > 0)
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count));
        return SurvivorOrbData.HasActivePair(boardItemIds, color, out pairTier);
    }

    /// <summary>
    ///     전체 아이템 목록
    /// </summary>
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

    /// <summary>
    ///     플레이어 인벤토리 제거 (플레이어 퇴장 시)
    /// </summary>
    public void RemovePlayerInventory(long playerId)
    {
        _playerInventories.TryRemove(playerId, out _);
    }
}

/// <summary>
///     MatchingId별로 인게임 인벤토리 상태를 관리하는 매니저
///     각 매칭 인스턴스는 독립적인 상태를 가짐
/// </summary>
public class InGameInventoryManager
{
    private readonly ConcurrentDictionary<long, MatchingInventoryState> _matchingStates = new();
    private Action<string>? _logAction;

    public void Initialize(Action<string>? logAction = null)
    {
        _logAction = logAction;
        _matchingStates.Clear();
        _logAction?.Invoke("InGameInventoryManager: Initialized");
    }

    /// <summary>
    ///     매칭 인스턴스의 상태를 가져오거나 새로 생성
    /// </summary>
    private MatchingInventoryState GetOrCreateMatchingState(long matchingId)
    {
        return _matchingStates.GetOrAdd(matchingId, id =>
        {
            _logAction?.Invoke($"InGameInventoryManager: Creating new state for MatchingId={id}");
            return new MatchingInventoryState(id);
        });
    }

    /// <summary>
    ///     플레이어 인벤토리 가져오기
    /// </summary>
    public PlayerInGameInventory GetPlayerInventory(long matchingId, long playerId)
    {
        var matchingState = GetOrCreateMatchingState(matchingId);
        return matchingState.GetOrCreatePlayerInventory(playerId);
    }

    /// <summary>
    ///     보드 위 오브 수 (스택 포함). 개봉 비용 비례식의 입력 — 머지가 이 수를 줄여
    ///     다음 개봉을 싸게 한다.
    /// </summary>
    public int CountOrbs(long matchingId, long playerId)
    {
        return GetPlayerInventory(matchingId, playerId)
            .GetAllItems()
            .Where(item => item.Count > 0 &&
                           (SurvivorOrbData.IsRecoveryOrb(item.ItemId) ||
                            SurvivorOrbData.TryGetColorAndTier(item.ItemId, out _, out _)))
            .Sum(item => item.Count);
    }

    /// <summary>
    ///     아이템 추가
    /// </summary>
    public InGameItemInfo AddItem(long matchingId, long playerId, int itemId, int count = 1,
        GiftState giftState = GiftState.None)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        var item = inventory.AddItem(itemId, count, giftState);
        _logAction?.Invoke(
            $"InGameInventoryManager: Added item (MatchingId={matchingId}, PlayerId={playerId}, ItemId={itemId}, Count={count}, GiftState={giftState}, ItemUid={item.ItemUid})");
        return item;
    }

    public int EnsureItemCount(long matchingId, long playerId, int itemId, int minCount,
        GiftState giftState = GiftState.None)
    {
        if (minCount <= 0)
            return 0;

        var inventory = GetPlayerInventory(matchingId, playerId);
        int currentCount = inventory.GetItemCount(itemId);
        int addCount = minCount - currentCount;
        if (addCount <= 0)
            return 0;

        var item = inventory.AddItem(itemId, addCount, giftState);
        _logAction?.Invoke(
            $"InGameInventoryManager: Ensured item count (MatchingId={matchingId}, PlayerId={playerId}, ItemId={itemId}, MinCount={minCount}, AddedCount={addCount}, GiftState={giftState}, ItemUid={item.ItemUid})");
        return addCount;
    }

    /// <summary>
    ///     아이템 사용/제거
    /// </summary>
    public bool TryRemoveItem(long matchingId, long playerId, long itemUid, int count, out InGameItemInfo? updatedItem)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        bool result = inventory.TryRemoveItem(itemUid, count, out updatedItem);
        if (result)
            _logAction?.Invoke(
                $"InGameInventoryManager: Removed item (MatchingId={matchingId}, PlayerId={playerId}, ItemUid={itemUid}, Count={count})");
        return result;
    }

    public bool TryAddItemWithCapacity(long matchingId, long playerId, int itemId, int maxSlots,
        out InGameItemInfo? addedItem, GiftState giftState = GiftState.None)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        bool result = inventory.TryAddItemWithCapacity(itemId, maxSlots, out addedItem, giftState);
        if (result && addedItem != null)
            _logAction?.Invoke($"InGameInventoryManager: Added capacity-limited item (MatchingId={matchingId}, PlayerId={playerId}, ItemId={itemId}, ItemUid={addedItem.ItemUid}, MaxSlots={maxSlots})");
        return result;
    }
    /// <summary>
    ///     ItemId로 아이템 제거 (탈출 아이템 전달용)
    /// </summary>
    public bool TryRemoveOneByItemId(long matchingId, long playerId, int itemId, out InGameItemInfo? updatedItem)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        bool result = inventory.TryRemoveOneByItemId(itemId, out updatedItem);
        if (result)
            _logAction?.Invoke(
                $"InGameInventoryManager: Removed one item by ItemId (MatchingId={matchingId}, PlayerId={playerId}, ItemId={itemId}, ItemUid={updatedItem?.ItemUid})");
        return result;
    }

    public bool TryCombineItems(long matchingId, long playerId, IReadOnlyCollection<int> inputItemIds,
        int outputItemId, out List<InGameItemInfo> changedItems)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        bool result = inventory.TryCombineItems(inputItemIds, outputItemId, out changedItems);
        if (result)
            _logAction?.Invoke(
                $"InGameInventoryManager: Combined items (MatchingId={matchingId}, PlayerId={playerId}, Inputs=[{string.Join(',', inputItemIds)}], Output={outputItemId})");
        return result;
    }

    public bool TryCombineSurvivorOrbs(long matchingId, long playerId, int inputA, int inputB,
        Random random, out int outputItemId, out List<InGameItemInfo> changedItems)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        bool result = inventory.TryCombineSurvivorOrbs(inputA, inputB, random, out outputItemId, out changedItems);
        if (result)
            _logAction?.Invoke(
                $"InGameInventoryManager: Random Survivor orb merge (MatchingId={matchingId}, PlayerId={playerId}, Inputs=[{inputA},{inputB}], Output={outputItemId})");
        return result;
    }
    public bool TryEquipBattleItem(long matchingId, long playerId, long itemUid,
        out InGameItemInfo? equippedItem)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        bool result = inventory.TryEquipBattleItem(itemUid, out equippedItem);
        if (result)
        {
            _logAction?.Invoke(
                $"InGameInventoryManager: Equipped battle item (MatchingId={matchingId}, PlayerId={playerId}, ItemId={equippedItem?.ItemId}, ItemUid={itemUid})");
        }

        return result;
    }

    public InGameItemInfo? GetEquippedBattleItem(long matchingId, long playerId)
    {
        return GetPlayerInventory(matchingId, playerId).GetEquippedBattleItem();
    }


    public InGameItemInfo? RemoveItemByItemId(long matchingId, long playerId, int itemId)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        var item = inventory.GetAllItems().FirstOrDefault(i => i.ItemId == itemId);
        if (item == null) return null;

        if (inventory.TryRemoveItem(item.ItemUid, item.Count, out _))
        {
            _logAction?.Invoke(
                $"InGameInventoryManager: Removed item by ItemId (MatchingId={matchingId}, PlayerId={playerId}, ItemId={itemId}, ItemUid={item.ItemUid})");
            return item;
        }

        return null;
    }

    /// <summary>
    ///     플레이어의 전체 아이템 목록
    /// </summary>
    public List<InGameItemInfo> GetAllItems(long matchingId, long playerId)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        return inventory.GetAllItems();
    }

    /// <summary>
    ///     매칭 종료 시 해당 매칭의 상태 정리
    /// </summary>
    public List<InGameItemInfo> TakeAllItems(long matchingId, long playerId)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        var items = inventory.TakeAllItems();
        _logAction?.Invoke(
            $"InGameInventoryManager: Dropped all items (MatchingId={matchingId}, PlayerId={playerId}, Slots={items.Count})");
        return items;
    }
    public void RemoveMatchingState(long matchingId)
    {
        if (_matchingStates.TryRemove(matchingId, out _))
            _logAction?.Invoke($"InGameInventoryManager: Removed state for MatchingId={matchingId}");
    }

    /// <summary>
    ///     전체 상태 초기화
    /// </summary>
    public void Reset()
    {
        _matchingStates.Clear();
        _logAction?.Invoke("InGameInventoryManager: All matching states cleared");
    }
}
