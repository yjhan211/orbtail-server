using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     매치 잠금 안에서 상자 비용·보상·쿨다운과 문 열기 조건을 처리한다.
///     연결의 START/FINISH 권리는 PlayerInteractionState가, 패킷 전송은 세션이 맡는다.
/// </summary>
internal static class MatchInteractionService
{
    public static (ErrorCode Error, int Remaining, bool Door) Start(
        MatchRuntime runtime, long playerId, AreaType area, int interactId)
    {
        RequireLock(runtime);
        var info = GameInteractableData.Get(interactId);
        if (info == null) return (ErrorCode.FATAL, 0, false);
        if (Config.IsSwarmExploreDisabled() && info.DoorId <= 0)
            return (ErrorCode.INVALID_GAME_STATE, 0, false);
        if (info.ZoneId != (int)area) return (ErrorCode.AREA_MISMATCH, 0, false);
        // 문은 전용 START/FINISH 경로에서만 처리한다.
        if (info.DoorId > 0) return (ErrorCode.INVALID_GAME_STATE, 0, false);
        if (runtime.SummonStones.GetSnapshot(playerId).StoneCount < Config.SWARM_BOX_OPEN_COST)
            return (ErrorCode.INSUFFICIENT_CURRENCY, 0, false);
        if (!runtime.CollectCooldowns.TryAcquireCooldown(interactId,
                RngCollectCooldownStore.DefaultCooldownSeconds, out int remaining))
            return (ErrorCode.ACTION_ALREADY_EXPLORED, remaining, false);
        return (ErrorCode.SUCCESS, 0, false);
    }

    public static int OpenBox(MatchRuntime runtime, long playerId, AreaType area, int interactId,
        Vector3f? position, Action stonesChanged, Action<IReadOnlyList<GroundItemInfo>> publishSpawn)
    {
        RequireLock(runtime);
        if (!runtime.SummonStones.TrySpendStones(playerId, Config.SWARM_BOX_OPEN_COST, out _))
        {
            runtime.CollectCooldowns.ClearCooldown(interactId);
            return 0;
        }
        // 재화 통지 → 보상 생성·통지 → 쿨다운 갱신 순서를 유지한다.
        stonesChanged();
        int itemId = Random.Shared.Next(100) < 60 ? Config.HEART_GROUND_ITEM_ID : Config.BOOTS_GROUND_ITEM_ID;
        if (position != null)
        {
            var spawned = runtime.GroundItems.SpawnItems(area, position.X, position.Y, [itemId],
                mapId: Config.SWARM_MATCH_MAP, layout: GroundItemSpawnLayout.EliminationScatter);
            publishSpawn(spawned);
        }
        runtime.CollectCooldowns.ClearCooldown(interactId);
        runtime.CollectCooldowns.TryAcquireCooldown(interactId, Config.SWARM_EXPLORE_REGEN_SECONDS, out _);
        return itemId;
    }

    public static ErrorCode CheckDoorGauge(MatchRuntime runtime, AreaType area, int doorId)
    {
        RequireLock(runtime);
        if (runtime.Doors.IsDoorOpen(doorId)) return ErrorCode.DOOR_ALREADY_OPEN;
        var door = GameDoorData.Get(doorId);
        if (door != null && IsBlockedByClosure(runtime, area, door.AreaType, door.AreaTypeB))
            return ErrorCode.INVALID_GAME_STATE;
        return ErrorCode.SUCCESS;
    }

    public static ErrorCode OpenDoor(MatchRuntime runtime, long playerId, AreaType area, int doorId)
    {
        RequireLock(runtime);
        var door = GameDoorData.Get(doorId);
        if (door == null || IsBlockedByClosure(runtime, area, door.AreaType, door.AreaTypeB))
            return ErrorCode.INVALID_GAME_STATE;
        if (GameInteractableData.IsGaugeGatedDoor(doorId) && !runtime.Doors.IsDoorOpen(doorId))
            return ErrorCode.DOOR_KEY_MISSING;
        if (runtime.Doors.IsDoorOpen(doorId)) return ErrorCode.DOOR_ALREADY_OPEN;
        if (door.RequiredItemId > 0 &&
            runtime.Inventory.GetPlayerInventory(playerId).GetItemCount(door.RequiredItemId) <= 0)
            return ErrorCode.DOOR_KEY_MISSING;
        runtime.Doors.OpenDoor(doorId);
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
        if (!Monitor.IsEntered(runtime.Sync))
            throw new InvalidOperationException("Interaction changes require the match lock.");
    }
}
