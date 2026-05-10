using System.Collections.Concurrent;
using network.common;
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
    public InGameItemInfo AddItem(int itemId, int count = 1, GiftState giftState = GiftState.None)
    {
        // 같은 itemId와 선물 상태가 있으면 수량 추가 (스택)
        foreach (var kvp in _items)
            if (kvp.Value.ItemId == itemId && kvp.Value.GiftState == giftState)
            {
                kvp.Value.Count += count;
                return kvp.Value;
            }

        // 없으면 새로 생성
        var newItem = new InGameItemInfo { ItemUid = GenerateUid(), ItemId = itemId, Count = count, GiftState = giftState };
        _items[newItem.ItemUid] = newItem;
        return newItem;
    }

    /// <summary>
    ///     아이템 사용/제거
    /// </summary>
    /// <returns>성공 여부와 변경된 아이템 정보 (삭제된 경우 Count=0)</returns>
    public bool TryRemoveItem(long itemUid, int count, out InGameItemInfo? updatedItem)
    {
        updatedItem = null;

        if (!_items.TryGetValue(itemUid, out var item)) return false;

        if (item.Count < count) return false;

        item.Count -= count;

        if (item.Count <= 0)
        {
            _items.TryRemove(itemUid, out _);
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

    /// <summary>
    ///     특정 아이템 조회
    /// </summary>
    public InGameItemInfo? GetItem(long itemUid)
    {
        return _items.GetValueOrDefault(itemUid);
    }

    /// <summary>
    ///     전체 아이템 목록
    /// </summary>
    public List<InGameItemInfo> GetAllItems()
    {
        return _items.Values.ToList();
    }

    /// <summary>
    ///     특정 종류의 아이템 수량 합계
    /// </summary>
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

    /// <summary>
    ///     ItemId로 아이템 제거 (탈출 아이템 전달용)
    /// </summary>
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
