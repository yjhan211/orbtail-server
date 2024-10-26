using network.common;

namespace network.helpers
{
    public static class SubjectHelper
    {
        private const string MOVE_MANAGE = "move_object";
        private const string LEAVE_OBJECT = "leave_object";
        private const string SPAWN_OBJECT = "spawn_object";
        private const string DESTROY_OBJECT = "destroy_object";
        private const string UPDATE_INFO = "update_info";
        private const string CREATE_JOB_RESOURCE = "create_job_resource";
        private const string BROADCAST_UPDATE = "broadcast_update";
        private const string BROADCAST_DESTROY = "broadcast_destroy";

        private static string BuildSubject(string prefix, MapID mapId, long mapSubId, int serverId)
            => $"{prefix}_{mapId}_{mapSubId}_{serverId}";

        public static string GetEnterInstanceSubject(int serverId) => $"create_instance_{serverId}";

        public static string GetMoveManageSubject(GameObjectInfo objectInfo, int serverId)
            => BuildSubject(MOVE_MANAGE, objectInfo.MapId, objectInfo.MapSubId, serverId);

        public static string GetMoveManageSubject(MapID mapId, long mapSubId, int serverId)
            => BuildSubject(MOVE_MANAGE, mapId, mapSubId, serverId);

        public static string GetLeaveManageSubject(GameObjectInfo objectInfo, int serverId)
            => BuildSubject(LEAVE_OBJECT, objectInfo.MapId, objectInfo.MapSubId, serverId);

        public static string GetLeaveManageSubject(MapID mapId, long mapSubId, int serverId)
            => BuildSubject(LEAVE_OBJECT, mapId, mapSubId, serverId);

        public static string GetSpawnManageSubject(GameObjectInfo objectInfo, int serverId)
            => BuildSubject(SPAWN_OBJECT, objectInfo.MapId, objectInfo.MapSubId, serverId);

        public static string GetSpawnManageSubject(MapID mapId, long mapSubId, int serverId)
            => BuildSubject(SPAWN_OBJECT, mapId, mapSubId, serverId);

        public static string GetDestroyObjectSubject(GameObjectInfo objectInfo, int serverId)
            => BuildSubject(DESTROY_OBJECT, objectInfo.MapId, objectInfo.MapSubId, serverId);

        public static string GetDestroyObjectSubject(MapID mapId, long mapSubId, int serverId)
            => BuildSubject(DESTROY_OBJECT, mapId, mapSubId, serverId);

        public static string GetUpdateInfoSubject(MapID mapId, long mapSubId, int serverId)
            => BuildSubject(UPDATE_INFO, mapId, mapSubId, serverId);

        public static string GetUpdateInfoSubject(GameObjectInfo objectInfo, int serverId)
            => BuildSubject(UPDATE_INFO, objectInfo.MapId, objectInfo.MapSubId, serverId);

        public static string GetCreateJobResourceSubject(MapID mapId, long mapSubId, int serverId)
            => BuildSubject(CREATE_JOB_RESOURCE, mapId, mapSubId, serverId);

        public static string GetBroadcastUpdateSubject(MapID mapId, long mapSubId, int serverId)
            => BuildSubject(BROADCAST_UPDATE, mapId, mapSubId, serverId);

        public static string GetBroadcastDestroySubject(MapID mapId, long mapSubId, int serverId)
            => BuildSubject(BROADCAST_DESTROY, mapId, mapSubId, serverId);
    }
}