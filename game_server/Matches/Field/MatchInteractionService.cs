using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.field;

/// <summary>
///     매치 잠금 안에서 문 열기 조건과 진행 중인 상호작용 취소를 처리한다.
///     연결의 START/FINISH 권리는 PlayerInteractionState가, 패킷 전송은 세션이 맡는다.
/// </summary>
internal static class MatchInteractionService
{
    /// <summary>
    ///     진행 중인 문 상호작용을 정리하고 취소한 ID를 반환한다.
    ///     호출자는 매치 잠금을 유지한 채 반환된 ID로 로그와 취소 알림을 보낸다.
    /// </summary>
    public static int[] CancelPendingInteractions(MatchRuntime runtime, PlayerInteractionState state)
    {
        RequireLock(runtime);
        int[] canceledIds = state.Snapshot();
        state.Clear();
        return canceledIds;
    }

    public static ErrorCode CheckDoorGauge(MatchRuntime runtime, AreaType area, int doorId)
    {
        RequireLock(runtime);
        if (runtime.Doors.IsDoorOpen(doorId)) return ErrorCode.DOOR_ALREADY_OPEN;
        var door = GameDoorData.Get(doorId);
        if (door == null || IsBlockedByClosure(runtime, area, door.AreaType, door.AreaTypeB))
            return ErrorCode.INVALID_GAME_STATE;
        return ErrorCode.SUCCESS;
    }

    public static bool FinishDoor(MatchRuntime runtime, PlayerInteractionState state, int doorId)
    {
        RequireLock(runtime);
        state.CompleteDoor();
        return runtime.Doors.OpenDoor(doorId);
    }

    private static bool IsBlockedByClosure(MatchRuntime runtime, AreaType current, AreaType first, AreaType second) =>
        (runtime.Closures.IsAreaClosed(first) || runtime.Closures.IsAreaClosed(second)) &&
        !runtime.Closures.IsAreaClosed(current);

    private static void RequireLock(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
            throw new InvalidOperationException("Interaction changes require the match lock.");
    }
}
