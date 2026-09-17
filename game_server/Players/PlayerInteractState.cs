using network.common;

namespace game_server.players;

/// <summary>플레이어의 진행 중인 상호작용과 시작 시각. 호출자는 매치 잠금을 보유한다.</summary>
internal sealed class PlayerInteractState
{
    public int? PendingInteractId { get; private set; }
    private DateTime _startedAtUtc;

    public void Begin(int interactId, DateTime nowUtc)
    {
        PendingInteractId = interactId;
        _startedAtUtc = nowUtc;
    }

    public bool Cancel(int interactId)
    {
        if (PendingInteractId != interactId)
        {
            return false;
        }
        Cancel();
        return true;
    }

    public int? Cancel()
    {
        int? canceledId = PendingInteractId;
        PendingInteractId = null;
        _startedAtUtc = default;
        return canceledId;
    }

    public bool TryComplete(int interactId, DateTime nowUtc, TimeSpan duration, out ErrorCode error)
    {
        error = ErrorCode.INVALID_GAME_STATE;
        if (PendingInteractId != interactId)
        {
            return false;
        }
        if (nowUtc - _startedAtUtc < duration)
        {
            error = ErrorCode.DOOR_OPEN_TOO_EARLY;
            return false;
        }
        Cancel();
        error = ErrorCode.SUCCESS;
        return true;
    }
}
