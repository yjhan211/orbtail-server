using System;
using network.common;
using network.common.data.models;

namespace network.common.data
{

    /// <summary>
    /// Converts between shared map cells and Unity world positions. The server has no
    /// Tilemap instance, so the Grid transform authored in map_info.csv is authoritative.
    /// </summary>
    public static class MapCoordinateConverter
    {
        private const float IsometricCellCenterOffsetY = 0.25f;

        public static Cell WorldToCell(MapId mapId, Vector3f worldPosition)
        {
            var (originX, originY) = GetGridOrigin(mapId);
            float localX = worldPosition.X - originX;
            float localY = worldPosition.Y - originY;
            return new Cell(
                (int)Math.Floor(localX + 2f * localY),
                (int)Math.Floor(2f * localY - localX));
        }

        public static Vector3f CellToWorld(MapId mapId, Cell cell)
        {
            var (originX, originY) = GetGridOrigin(mapId);
            return new Vector3f(
                (cell.X - cell.Y) * 0.5f + originX,
                (cell.X + cell.Y) * 0.25f + IsometricCellCenterOffsetY + originY,
                0f);
        }

        public static (float X, float Y) CellToWorldPoint(MapId mapId, Cell cell)
        {
            var world = CellToWorld(mapId, cell);
            return (world.X, world.Y);
        }

        /// <summary>from 구역 스폰 중심에서 to 구역 스폰 중심을 향한 단위 벡터. 같은 구역이거나 겹치면 영벡터.</summary>
        public static Vector3f GetAreaDirection(MapId mapId, AreaType from, AreaType to)
        {
            if (from == to)
            {
                return new Vector3f(0f, 0f, 0f);
            }

            var origin = CellToWorld(mapId, GameMapData.GetAreaSpawnCell(mapId, from));
            var target = CellToWorld(mapId, GameMapData.GetAreaSpawnCell(mapId, to));
            float dx = target.X - origin.X;
            float dy = target.Y - origin.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            return length < 0.001f ? new Vector3f(0f, 0f, 0f) : new Vector3f(dx / length, dy / length, 0f);
        }

        private static (float X, float Y) GetGridOrigin(MapId mapId)
        {
            var mapInfo = GameMapData.GetMapInfo(mapId);
            return mapInfo is null ? (0f, 0f) : (mapInfo.WorldOriginX, mapInfo.WorldOriginY);
        }
    }
}
