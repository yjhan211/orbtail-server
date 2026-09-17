using System;
using network.common;
using network.common.data.models;

namespace network.common.data
{

    /// <summary>
    ///     서버와 클라이언트가 공유하는 맵의 셀 좌표와 월드 좌표를 변환한다.
    ///     map_info.csv의 원점을 기준으로 아이소메트릭 격자 변환을 적용한다.
    /// </summary>
    public static class MapCoordinateConverter
    {
        private const float IsometricCellCenterOffsetY = 0.25f;

        public static Cell WorldToCell(MapId mapId, Vector3f worldPosition)
        {
            var (originX, originY) = GetGridOrigin(mapId);
            float localX = worldPosition.X - originX;
            float localY = worldPosition.Y - originY;
            return new Cell((int)Math.Floor(localX + 2f * localY), (int)Math.Floor(2f * localY - localX));
        }

        public static Vector3f CellToWorld(MapId mapId, Cell cell)
        {
            var (originX, originY) = GetGridOrigin(mapId);
            return new Vector3f((cell.X - cell.Y) * 0.5f + originX, (cell.X + cell.Y) * 0.25f + IsometricCellCenterOffsetY + originY, 0f);
        }

        private static (float X, float Y) GetGridOrigin(MapId mapId)
        {
            var mapInfo = GameMapData.GetMapInfo(mapId);
            return (mapInfo.WorldOriginX, mapInfo.WorldOriginY);
        }
    }
}
