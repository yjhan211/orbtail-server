using game_server.matches;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     매치 잠금 안에서 플레이어의 상호작용 상태를 시작·취소·완료한다.
/// </summary>
internal sealed class PlayerInteractionService
{
    public int? CancelPendingInteractions(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Interaction changes require the match lock.");
        }
        return player.Interactions.Cancel();
    }

    public ErrorCode StartDoor(MatchRuntime runtime, Player player, int interactId, int doorId, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Interaction changes require the match lock.");
        }
        if (runtime.IsEnded || player.IsEliminated)
        {
            return ErrorCode.INVALID_GAME_STATE;
        }

        var error = ValidateDoor(runtime, player, interactId, doorId);
        if (error == ErrorCode.SUCCESS)
        {
            player.Interactions.Begin(interactId, nowUtc);
        }
        return error;
    }

    public bool TryFinishDoor(MatchRuntime runtime, Player player, int interactId, int doorId, DateTime nowUtc, out ErrorCode error)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Interaction changes require the match lock.");
        }

        error = ErrorCode.INVALID_GAME_STATE;
        if (!runtime.IsEnded && !player.IsEliminated)
        {
            error = ValidateDoor(runtime, player, interactId, doorId);
        }
        if (error != ErrorCode.SUCCESS)
        {
            player.Interactions.Cancel(interactId);
            return false;
        }

        if (!player.Interactions.TryComplete(interactId, nowUtc, TimeSpan.FromSeconds(Config.GetSwarmDoorGaugeSeconds(doorId)), out error))
        {
            return false;
        }
        if (!runtime.Doors.OpenDoor(doorId))
        {
            error = ErrorCode.DOOR_ALREADY_OPEN;
            return false;
        }
        return true;
    }

    public List<InteractableInfo> GetInteractionInfos(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Interaction changes require the match lock.");
        }

        var available = new List<InteractableInfo>();
        foreach (var definition in GameInteractableData.GetAll())
        {
            if (definition.DoorId <= 0)
            {
                continue;
            }

            available.Add(new InteractableInfo { InteractId = definition.Id, IsCompleted = runtime.Doors.IsDoorOpen(definition.DoorId) });
        }
        return available;
    }

    internal ErrorCode ValidateDoor(MatchRuntime runtime, Player player, int interactId, int doorId)
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

        var playerArea = player.CurrentArea;
        if ((runtime.Closures.IsAreaClosed(door.AreaType) || runtime.Closures.IsAreaClosed(door.AreaTypeB)) && !runtime.Closures.IsAreaClosed(playerArea))
        {
            return ErrorCode.INVALID_GAME_STATE;
        }
        var info = GameInteractableData.Get(interactId);
        if (info == null || info.DoorId != doorId)
        {
            return ErrorCode.INVALID_GAME_STATE;
        }
        if (info.ZoneId != (int)playerArea)
        {
            return ErrorCode.AREA_MISMATCH;
        }

        var cell = player.Cell;
        if (cell == null || cell.X != info.CellX || cell.Y != info.CellY)
        {
            return ErrorCode.DOOR_TOO_FAR;
        }
        return ErrorCode.SUCCESS;
    }
}
