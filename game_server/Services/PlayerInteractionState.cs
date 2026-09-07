namespace game_server.services;

/// <summary>
///     플레이어 하나의 START/FINISH 대기와 문 게이지 상태. 세션이 소유하고 매치 잠금 안에서 접근한다.
///     완료·취소로 지운 요청은 늦은 FINISH가 다시 사용할 수 없다.
/// </summary>
internal sealed class PlayerInteractionState
{
    private readonly HashSet<int> _pending = [];
    private int? _pendingDoor;
    private int _openedDoors;
    public int Count => _pending.Count;
    public void Begin(int interactId) => _pending.Add(interactId);
    public bool TryFinish(int interactId) => _pending.Remove(interactId);
    public int[] Snapshot() => _pending.ToArray();
    public void Clear() => _pending.Clear();

    public void BeginDoor(int interactId)
    {
        Begin(interactId);
        _pendingDoor = interactId;
    }

    public void CompleteDoor()
    {
        _pendingDoor = null;
        _openedDoors++;
    }

    public int? InterruptDoor()
    {
        // 첫 문은 시작 구역 탈출을 보장하기 위해 피격으로 중단하지 않는다.
        if (_pendingDoor is not { } id || _openedDoors == 0) return null;
        _pendingDoor = null;
        _pending.Remove(id);
        return id;
    }
}
