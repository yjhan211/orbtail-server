// ReSharper disable All
using System;
using System.Collections.Generic;
using network.common.data.models;

namespace network.common.data.helpers
{
    /// <summary>
    /// 월드 좌표(Vector3f) ↔ 타일 좌표(Cell) 변환
    /// </summary>
    public static class CoordinateConverter
    {
        // 타일 크기 설정 (1타일 = 2 유닛)
        // TODO: 맵마다 다른 타일 크기 지원 시 GameMapData에서 가져오기
        public const float TILE_SIZE = 2.0f;

        /// <summary>
        /// 월드 좌표 → 타일 좌표
        /// </summary>
        public static Cell WorldToCell(Vector3f worldPos)
        {
            // Floor를 사용하여 타일 인덱스 계산
            int cellX = (int)Math.Floor(worldPos.X / TILE_SIZE);
            int cellY = (int)Math.Floor(worldPos.Z / TILE_SIZE); // Unity는 Z가 전방

            return new Cell(cellX, cellY);
        }

        /// <summary>
        /// 타일 좌표 → 월드 좌표 (타일 중심점)
        /// </summary>
        public static Vector3f CellToWorld(Cell cell)
        {
            float worldX = cell.X * TILE_SIZE + TILE_SIZE * 0.5f;
            float worldZ = cell.Y * TILE_SIZE + TILE_SIZE * 0.5f;

            return new Vector3f(worldX, 0, worldZ);
        }

        /// <summary>
        /// 두 월드 좌표가 같은 타일에 있는지 체크
        /// </summary>
        public static bool IsInSameCell(Vector3f pos1, Vector3f pos2)
        {
            var cell1 = WorldToCell(pos1);
            var cell2 = WorldToCell(pos2);
            return cell1.Equals(cell2);
        }

        /// <summary>
        /// 타일 경계까지의 거리 계산 (경계 근처 감지용)
        /// </summary>
        public static float DistanceToTileBoundary(Vector3f worldPos)
        {
            var cell = WorldToCell(worldPos);
            var cellCenter = CellToWorld(cell);

            // 타일 중심으로부터의 거리
            float dx = Math.Abs(worldPos.X - cellCenter.X);
            float dz = Math.Abs(worldPos.Z - cellCenter.Z);

            // 경계까지의 최소 거리
            float distToBoundaryX = TILE_SIZE * 0.5f - dx;
            float distToBoundaryZ = TILE_SIZE * 0.5f - dz;

            return Math.Min(distToBoundaryX, distToBoundaryZ);
        }

        /// <summary>
        /// 특정 반경 내의 타일 리스트 가져오기 (AOI용)
        /// </summary>
        public static List<Cell> GetCellsInRadius(Cell centerCell, int radius)
        {
            var cells = new List<Cell>();

            for (int x = -radius; x <= radius; x++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    cells.Add(new Cell(centerCell.X + x, centerCell.Y + y));
                }
            }

            return cells;
        }

        /// <summary>
        /// 월드 좌표가 타일 내 어느 위치에 있는지 정규화 (0~1)
        /// </summary>
        public static (float normalizedX, float normalizedY) GetNormalizedPositionInCell(Vector3f worldPos)
        {
            var cell = WorldToCell(worldPos);
            float cellMinX = cell.X * TILE_SIZE;
            float cellMinZ = cell.Y * TILE_SIZE;

            float normalizedX = (worldPos.X - cellMinX) / TILE_SIZE;
            float normalizedZ = (worldPos.Z - cellMinZ) / TILE_SIZE;

            return (normalizedX, normalizedZ);
        }
    }
}
