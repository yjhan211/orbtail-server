using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace network
{
    public static class MapHelper
    {
        public const int MAP_SIZE = 100;

        public static bool IsOutOfMapRange(Cell cell)
        {
            return cell.x < 0 || cell.x >= MAP_SIZE || cell.y < 0 || cell.y >= MAP_SIZE;
        }

        public static Cell GetRandomCell()
        {
            Random random = new();
            return new(random.Next(0, MAP_SIZE), random.Next(0, MAP_SIZE));
        }

        public static string GetPositionKey(int map_id, Cell cell)
        {
            return $"map_{map_id}|{cell.x},{cell.y}";
        }

        public static Cell GetCell(string position_key)
        {
            var split = position_key.Split("|");
            var position = split[1].Split(",");

            return new Cell(Int32.Parse(position[0]), Int32.Parse(position[1]));
        }

        public static string GetMapKey()
        {
            return $"map_{1}";
        }

        public static string GetMapKey(int map_id)
        {
            return $"map_{map_id}";
        }

        public static string GetPositionKey(Cell cell)
        {
            return $"{GetMapKey()}|{cell.x},{cell.y}";
        }

        public static Cell CalcTargetCell(Cell cell, DirectionType direction)
        {
            Cell clone = Cell.Clone(cell);

            switch (direction)
            {
                case DirectionType.TOP_LEFT:
                    clone.x += 1;
                    break;

                case DirectionType.TOP_RIGHT:
                    clone.x -= 1;
                    break;

                case DirectionType.BOTTOM_LEFT:
                    clone.y -= 1;
                    break;

                case DirectionType.BOTTOM_RIGHT:
                    clone.y += 1;
                    break;
            }

            return clone;
        }

        public static List<Cell> GetBoundCellList(Cell pivot_cell)
        {
            List<Cell> result = new();

            int min_x = pivot_cell.x - 13;
            int max_x = pivot_cell.x + 14;

            int min_y = pivot_cell.y - 7;
            int max_y = pivot_cell.y - 9;

            int line = 0;

            for (int x = min_x; x <= max_x; x++)
            {
                line += 1;

                if (line <= 1)
                {
                    min_y -= 1;
                    max_y += 2;
                }
                else if (line <= 4)
                {
                    min_y -= 1;
                    max_y += 1;
                }
                else if (line == 5)
                {
                    min_y -= 1;
                    max_y += 1;
                }
                else if (line == 6)
                {
                    min_y -= 1;
                    max_y += 1;
                }
                else if (line == 7)
                {
                    max_y += 1;
                }
                else if (line == 8)
                {
                    min_y += 1;
                    max_y += 1;
                }
                else if (line == 9)
                {
                    min_y += 1;
                    max_y += 1;
                }
                else if (line <= 22)
                {
                    min_y += 1;
                    max_y += 1;
                }
                else if (line == 23)
                {
                    min_y += 1;
                }
                else
                {
                    min_y += 1;
                    max_y -= 1;
                }

                for (int y = min_y; y <= max_y; y++)
                {
                    Cell cell = new(x, y);

                    if (IsOutOfMapRange(cell))
                    {
                        continue;
                    }

                    result.Add(cell);
                }
            }

            return result;
        }

        public static int CalcServerIdFromCell(Cell cell, int game_server_num)
        {
            // 맵 분할 설정
            int horizontal_divisions = 2; // 가로로 2개 섹션
            int vertical_divisions = 5; // 세로로 5개 섹션

            // 각 섹션의 크기
            int section_width = MapHelper.MAP_SIZE / horizontal_divisions;
            int section_height = MapHelper.MAP_SIZE / vertical_divisions;

            // 주어진 좌표
            int x = cell.x; // 예시 좌표 x
            int y = cell.y; // 예시 좌표 y

            // 주어진 좌표가 위치한 섹션의 가로, 세로 위치 계산
            int horizontal_position = x / section_width;
            int vertical_position = y / section_height;

            // 서버 ID 계산
            // 세로 위치(verticalPosition)를 기반으로 몇 번째 "행"에 있는지 계산하고,
            // 가로 위치(horizontalPosition)를 추가하여 최종적인 서버 ID를 도출
            int serverId = vertical_position * horizontal_divisions + horizontal_position + 1;

            return serverId;
        }
    }
}
