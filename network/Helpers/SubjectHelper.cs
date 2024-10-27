using network.common;
using network.common.models;

namespace network.helpers;

public static class SubjectHelper
{
    private const string UpdateObject = "move_object";
    private const string LeaveObject = "leave_object";
    private const string SpawnObject = "spawn_object";
    private const string DestroyObject = "destroy_object";
    private const string UpdateInfo = "update_info";
    private const string BroadcastUpdate = "broadcast_update";
    private const string BroadcastDestroy = "broadcast_destroy";

    private const string CreateJobResource = "create_job_resource"; // TODO

    private static string BuildSubject(string prefix, MapId mapId, long mapSubId, int serverId)
    {
        return $"{prefix}_{mapId}_{mapSubId}_{serverId}";
    }

    public static string GetEnterInstanceSubject(int serverId)
    {
        return $"create_instance_{serverId}";
    }

    public static string GetUpdateManageSubject(GameObjectInfo objectInfo, int serverId)
    {
        return BuildSubject(UpdateObject, objectInfo.MapId, objectInfo.MapSubId, serverId);
    }

    public static string GetUpdateManageSubject(MapId mapId, long mapSubId, int serverId)
    {
        return BuildSubject(UpdateObject, mapId, mapSubId, serverId);
    }

    public static string GetLeaveManageSubject(GameObjectInfo objectInfo, int serverId)
    {
        return BuildSubject(LeaveObject, objectInfo.MapId, objectInfo.MapSubId, serverId);
    }

    public static string GetLeaveManageSubject(MapId mapId, long mapSubId, int serverId)
    {
        return BuildSubject(LeaveObject, mapId, mapSubId, serverId);
    }

    public static string GetSpawnManageSubject(GameObjectInfo objectInfo, int serverId)
    {
        return BuildSubject(SpawnObject, objectInfo.MapId, objectInfo.MapSubId, serverId);
    }

    public static string GetSpawnManageSubject(MapId mapId, long mapSubId, int serverId)
    {
        return BuildSubject(SpawnObject, mapId, mapSubId, serverId);
    }

    public static string GetDestroyObjectSubject(GameObjectInfo objectInfo, int serverId)
    {
        return BuildSubject(DestroyObject, objectInfo.MapId, objectInfo.MapSubId, serverId);
    }

    public static string GetDestroyObjectSubject(MapId mapId, long mapSubId, int serverId)
    {
        return BuildSubject(DestroyObject, mapId, mapSubId, serverId);
    }

    public static string GetUpdateInfoSubject(MapId mapId, long mapSubId, int serverId)
    {
        return BuildSubject(UpdateInfo, mapId, mapSubId, serverId);
    }

    public static string GetUpdateInfoSubject(GameObjectInfo objectInfo, int serverId)
    {
        return BuildSubject(UpdateInfo, objectInfo.MapId, objectInfo.MapSubId, serverId);
    }

    public static string GetCreateJobResourceSubject(MapId mapId, long mapSubId, int serverId)
    {
        return BuildSubject(CreateJobResource, mapId, mapSubId, serverId);
    }

    public static string GetBroadcastUpdateSubject(MapId mapId, long mapSubId, int serverId)
    {
        return BuildSubject(BroadcastUpdate, mapId, mapSubId, serverId);
    }

    public static string GetBroadcastDestroySubject(MapId mapId, long mapSubId, int serverId)
    {
        return BuildSubject(BroadcastDestroy, mapId, mapSubId, serverId);
    }
}