using network.common;

namespace network.helpers
{
    public static class InstanceMapHelper
    {
        private static int _totalServerNum;
        private static readonly Dictionary<string, (MapID, Cell, bool)> _portalInfo = new()
        {
            // { GetPortalKey(MapID.LAB_1, new(91, 100)), (MapID.CAMPUS_1, new(110, 91), false) }
        };

        public static void Initialize(int totalServerNum)
        {
            _totalServerNum = totalServerNum;
        }

        public static string CreatePartKey(MapID mapId, long mapSubId) => $"map_{mapId}|{mapSubId}";
        private static string GetPortalKey(MapID mapId, Cell cell) => $"{mapId}|{cell.X},{cell.Y}";
        public static (MapID mapId, Cell spawnPosition, bool isFlip)? GetPortalOrNull(GameObjectInfo objectInfo)
        {
            string portalKey = GetPortalKey(objectInfo.MapId, objectInfo.TargetCell);
            if (_portalInfo.TryGetValue(portalKey, out var PortalInfo))
            {
                return PortalInfo;
            }

            return null;
        }

        public static int GetManageServerId(long mapSubId)
        {
            return (int)((mapSubId - 1) % _totalServerNum) + 1;
        }
    }
}