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

    /// <summary>
    ///     플레이어가 지금 있는 구역에서 할 수 있는 상호작용을 클라이언트에 보낼 형태로 돌려준다.
    ///     기본 상태 액션이 없는 정의, 탐색이 꺼져 있으면 문이 없는 정의, 이미 열린 문은 뺀다.
    ///     호출마다 새 DTO를 만들어 응답을 고쳐도 다른 세션에 영향이 없다.
    /// </summary>
    public List<InteractableObjectState> GetAvailableInteractions(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Available interactions require the match lock.");
        }

        var available = new List<InteractableObjectState>();
        foreach (var definition in GameInteractableData.GetAll())
        {
            if (definition.ZoneId != (int)player.CurrentArea)
            {
                continue;
            }

            bool hasDefaultAction = definition.Actions.Any(action => action.State == 0);
            if (!hasDefaultAction)
            {
                continue;
            }

            // 탐색 세대 상호작용은 문만 남긴다. 이미 연 문은 다시 보이지 않는다.
            bool isDoor = definition.DoorId > 0;
            if (Config.IsSwarmExploreDisabled() && !isDoor)
            {
                continue;
            }

            if (isDoor && runtime.Doors.IsDoorOpen(definition.DoorId))
            {
                continue;
            }

            var interaction = new InteractableObjectState { InteractId = definition.Id };
            foreach (var action in definition.Actions)
            {
                interaction.Actions.Add(new InteractableActionState
                {
                    Order = action.ActionId,
                    IsExplored = false,
                    ExploredBy = 0,
                    State = action.State
                });
            }
            available.Add(interaction);
        }
        return available;
    }
}
