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
    ///     오브 갈아끼우기 (#232 4단계): ItemUid·열 순번은 그대로 두고 ItemId만 바꾼다 — 합성 결과가
    ///     첫 원본 슬롯에, 예비 오브가 고른 슬롯에 들어간다. 제거+추가로 하면 열 끝으로 밀린다.
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

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryCombineOrbs(int inputA, int inputB, Random random,
        out int outputItemId, out List<InGameItemInfo> changedItems)
    {
        outputItemId = 0;
        changedItems = new List<InGameItemInfo>();
        if (!OrbData.CanMerge(inputA, inputB) || !HasItems([inputA, inputB]))
            return false;
        if (!OrbData.TryGetRandomMergeOutput(inputA, inputB, random, out outputItemId))
            return false;

        return TryCombineItems([inputA, inputB], outputItemId, out changedItems);
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
///     매치 런타임의 인벤토리에 아이템 추가·소비·조합 규칙을 적용한다.
///     매치별 사전은 소유하지 않으며, 종료된 매치의 인벤토리를 다시 만들지 않는다.
/// </summary>
public class InGameInventoryManager
{
    private readonly Func<long, MatchRuntime?> _getMatch;
    private Action<string>? _logAction;

    internal InGameInventoryManager(Func<long, MatchRuntime?> getMatch) => _getMatch = getMatch;

    public void Initialize(Action<string>? logAction = null)
    {
        _logAction = logAction;
        _logAction?.Invoke("InGameInventoryManager: Initialized");
    }

    /// <summary>
    ///     이미 생성된 매치의 인벤토리 상태를 조회한다.
    /// </summary>
    private MatchingInventoryState GetMatchingState(long matchingId) =>
        _getMatch(matchingId)?.Inventory
        ?? throw new InvalidOperationException($"Match is not available: {matchingId}");

    /// <summary>
    ///     플레이어 인벤토리 가져오기
    /// </summary>
    public PlayerInGameInventory GetPlayerInventory(long matchingId, long playerId)
    {
        var matchingState = GetMatchingState(matchingId);
        return matchingState.GetOrCreatePlayerInventory(playerId);
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

    public bool TryCombineOrbs(long matchingId, long playerId, int inputA, int inputB,
        Random random, out int outputItemId, out List<InGameItemInfo> changedItems)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        bool result = inventory.TryCombineOrbs(inputA, inputB, random, out outputItemId, out changedItems);
        if (result)
            _logAction?.Invoke(
                $"InGameInventoryManager: Random Swarm orb merge (MatchingId={matchingId}, PlayerId={playerId}, Inputs=[{inputA},{inputB}], Output={outputItemId})");
        return result;
    }

    public bool TryCombineRandomRecipe(
        long matchingId,
        long playerId,
        IReadOnlyList<BattleItemRecipe> candidates,
        Random random,
        out BattleItemRecipe? selectedRecipe,
        out List<InGameItemInfo> changedItems)
    {
        var inventory = GetPlayerInventory(matchingId, playerId);
        bool result = inventory.TryCombineRandomRecipe(
            candidates,
            random,
            out selectedRecipe,
            out changedItems);
        if (result)
            _logAction?.Invoke(
                $"InGameInventoryManager: Combined items (MatchingId={matchingId}, PlayerId={playerId}, Inputs=[{string.Join(',', selectedRecipe!.InputItemIds)}], Output={selectedRecipe.OutputItemId})");
        return result;
    }

    /// <summary>
    ///     #217 궤도 스쿼드 자동 머지: 같은 색·티어 3개가 모이면 같은 색 상위 티어로
    ///     즉시 합성한다 (SB 3머지 문법). 보드 관리를 실시간 태스크에서 제거하고,
    ///     드래프트(무슨 색을 쌓나)는 개봉 선택에 남는다. 반환은 변경 목록(클라 전송용).
    /// </summary>
    public List<InGameItemInfo> AutoMergeOrbs(long matchingId, long playerId)
    {
        var allChanged = new List<InGameItemInfo>();
        var inventory = GetPlayerInventory(matchingId, playerId);
        while (true)
        {
            int mergeItemId = inventory.GetAllItems()
                .Where(item => item.Count > 0)
                .GroupBy(item => item.ItemId)
                .Where(group => group.Sum(item => item.Count) >= 3 &&
                                TryGetTripleMergeOutput(group.Key, out _))
                .Select(group => group.Key)
                .FirstOrDefault();
            if (mergeItemId == 0)
                return allChanged;

            if (!TryGetTripleMergeOutput(mergeItemId, out int outputItemId) ||
                !inventory.TryCombineItems(
                    [mergeItemId, mergeItemId, mergeItemId], outputItemId, out var changedItems))
                return allChanged;

            allChanged.AddRange(changedItems);
            _logAction?.Invoke(
                $"InGameInventoryManager: Auto-merged orbs (MatchingId={matchingId}, PlayerId={playerId}, Input={mergeItemId}x3, Output={outputItemId})");
        }
    }

    /// <summary>같은 색 3개 → 같은 색 상위 티어. 회복 오브도 동일 규칙.</summary>
    private static bool TryGetTripleMergeOutput(int itemId, out int outputItemId)
    {
        outputItemId = 0;
        if (OrbData.TryGetRecoveryTier(itemId, out int recoveryTier))
        {
            outputItemId = recoveryTier switch
            {
                1 => 107000041,
                2 => 107000042,
                _ => 0
            };
            return outputItemId > 0;
        }

        return OrbData.TryGetColorAndTier(itemId, out var color, out int tier) &&
               tier < 3 &&
               OrbData.TryGetItemId(color, tier + 1, out outputItemId);
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

    /// <summary>장착 배틀아이템의 티어 (미장착 0) — 매치 정산의 최종 오브 티어 산정 공용 경로 (#297 중복 단일화).</summary>
    public int GetEquippedBattleItemTier(long matchingId, long playerId)
    {
        var equippedItem = GetEquippedBattleItem(matchingId, playerId);
        return equippedItem == null ? 0 : BattleItemCombatData.Get(equippedItem.ItemId)?.Tier ?? 0;
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
}
