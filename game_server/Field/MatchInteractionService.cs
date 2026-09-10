using game_server.matches;
using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.field;

/// <summary>
///     매치 잠금 안에서 문 열기 조건과 진행 중인 상호작용 취소를 처리한다.
///     참가자의 START/FINISH 상태는 Player가, 패킷 전송은 세션이 맡는다.
/// </summary>
internal static class MatchInteractionService
{
    /// <summary>
    ///     진행 중인 문 상호작용을 정리하고 취소한 ID를 반환한다.
    ///     호출자는 매치 잠금을 유지한 채 반환된 ID로 로그와 취소 알림을 보낸다.
    /// </summary>
    public static int[] CancelPendingInteractions(MatchRuntime runtime, Player state)
    {
        RequireLock(runtime);
        int[] canceledIds = state.GetPendingInteractionIds();
        state.ClearPendingInteractions();
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

    public static ErrorCode StartDoor(MatchRuntime runtime, Player player, int interactId, int doorId, long now)
    {
        RequireLock(runtime);
        if (runtime.IsEnded || player.IsEliminated) return ErrorCode.INVALID_GAME_STATE;
        var error = CheckDoorGauge(runtime, player.CurrentArea, doorId);
        if (error == ErrorCode.SUCCESS) player.BeginDoor(interactId, now);
        return error;
    }

    public static bool TryFinishDoor(MatchRuntime runtime, Player player, int interactId, int doorId, long now, out ErrorCode error)
    {
        RequireLock(runtime);
        error = runtime.IsEnded || player.IsEliminated
            ? ErrorCode.INVALID_GAME_STATE
            : CheckDoorGauge(runtime, player.CurrentArea, doorId);
        if (error != ErrorCode.SUCCESS)
        {
            player.TryFinishInteraction(interactId);
            return false;
        }
        if (!player.TryFinishDoor(interactId, now, TimeSpan.FromSeconds(Config.GetSwarmDoorGaugeSeconds(doorId)), out error))
            return false;
        if (!runtime.Doors.OpenDoor(doorId))
        {
            error = ErrorCode.DOOR_ALREADY_OPEN;
            return false;
        }
        player.CompleteDoor();
        return true;
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
