using game_server.matches;
using network.common;
using network.common.data;

namespace game_server.players;

/// <summary>
///     매치 잠금 안에서 플레이어의 상호작용 상태를 시작·취소·완료한다.
/// </summary>
internal sealed class PlayerInteractionService
{
    public int[] CancelPendingInteractions(MatchRuntime runtime, Player state)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Interaction changes require the match lock.");
        }
        int[] canceledIds = state.GetPendingInteractionIds();
        state.ClearPendingInteractions();
        return canceledIds;
    }

    public ErrorCode CheckDoorGauge(MatchRuntime runtime, AreaType area, int doorId)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Interaction changes require the match lock.");
        }

        if (runtime.Doors.IsDoorOpen(doorId))
        {
            return ErrorCode.DOOR_ALREADY_OPEN;
        }
        var door = GameDoorData.Get(doorId);
        if (door == null)
        {
            return ErrorCode.INVALID_GAME_STATE;
        }

        if ((runtime.Closures.IsAreaClosed(door.AreaType) || runtime.Closures.IsAreaClosed(door.AreaTypeB)) && !runtime.Closures.IsAreaClosed(area))
        {
            return ErrorCode.INVALID_GAME_STATE;
        }
        return ErrorCode.SUCCESS;
    }

    public ErrorCode StartDoor(MatchRuntime runtime, Player player, int interactId, int doorId, long now)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Interaction changes require the match lock.");
        }

        if (runtime.IsEnded || player.IsEliminated)
        {
            return ErrorCode.INVALID_GAME_STATE;
        }
        var error = CheckDoorGauge(runtime, player.CurrentArea, doorId);
        if (error == ErrorCode.SUCCESS)
        {
            player.BeginDoor(interactId, now);
        }
        return error;
    }

    public bool TryFinishDoor(MatchRuntime runtime, Player player, int interactId, int doorId, long now, out ErrorCode error)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Interaction changes require the match lock.");
        }

        error = runtime.IsEnded || player.IsEliminated ? ErrorCode.INVALID_GAME_STATE : CheckDoorGauge(runtime, player.CurrentArea, doorId);
        if (error != ErrorCode.SUCCESS)
        {
            player.TryFinishInteraction(interactId);
            return false;
        }

        if (!player.TryFinishDoor(interactId, now, TimeSpan.FromSeconds(Config.GetSwarmDoorGaugeSeconds(doorId)), out error))
        {
            return false;
        }
        if (!runtime.Doors.OpenDoor(doorId))
        {
            error = ErrorCode.DOOR_ALREADY_OPEN;
            return false;
        }

        player.CompleteDoor();
        return true;
    }
}
