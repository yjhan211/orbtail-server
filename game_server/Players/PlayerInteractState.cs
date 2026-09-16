using network.common;

namespace game_server.players;

/// <summary>플레이어의 진행 중인 상호작용과 시작 시각. 호출자는 매치 잠금을 보유한다.</summary>
internal sealed class PlayerInteractState
{
    public int? PendingInteractId { get; private set; }
    private long StartedAtTimestamp { get; set; }

    public void Begin(int interactId, long startedAtTimestamp)
    {
        PendingInteractId = interactId;
        StartedAtTimestamp = startedAtTimestamp;
    }

    public int[] GetPendingIds() => PendingInteractId.HasValue ? [PendingInteractId.Value] : [];

    public bool Cancel(int interactId)
    {
        if (PendingInteractId != interactId) return false;
        Cancel();
        return true;
    }

    public int? Cancel()
    {
        int? canceledId = PendingInteractId;
        PendingInteractId = null;
        StartedAtTimestamp = 0;
        return canceledId;
    }

    public bool TryComplete(int interactId, long now, TimeSpan duration, out ErrorCode error)
    {
        error = ErrorCode.INVALID_GAME_STATE;
        if (PendingInteractId != interactId) return false;
        if (now - StartedAtTimestamp < duration.TotalMilliseconds)
        {
            error = ErrorCode.DOOR_OPEN_TOO_EARLY;
            return false;
        }
        Cancel();
        error = ErrorCode.SUCCESS;
        return true;
    }
}
