namespace game_server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using network;

    public class MapController
    {
        const int MAP_SIZE = 100;
        const int X_MIN_BOUND = -11;
        const int X_MAX_BOUND = 14;
        const int Y_MIN_BOUND = -5;
        const int Y_MAX_BOUND = -6;
        readonly object map_info_lock;
        public readonly List<GameObject>[,] map_info;

        public MapController()
        {
            this.map_info_lock = new object();

            this.map_info = new List<GameObject>[MAP_SIZE, MAP_SIZE];
            for (int x = 0; x < MAP_SIZE; x++)
            {
                for (int y = 0; y < MAP_SIZE; y++)
                {
                    this.map_info[x, y] = new();
                }
            }
        }

        public void SpawnGameObject(GameObject game_object)
        {
            lock (this.map_info_lock)
            {
                var x = game_object.current_cell.x;
                var y = game_object.current_cell.y;

                this.map_info[x, y].Add(game_object);
            }
        }

        public void MoveGameObject(GameObject game_object)
        {
            lock (this.map_info_lock)
            {
                var current = game_object.current_cell;
                var target = game_object.target_cell;

                if (current.Equals(target))
                {
                    return;
                }

                this.map_info[current.x, current.y].Remove(game_object);
                this.map_info[target.x, target.y].Add(game_object);

                game_object.current_cell = target;
            }
        }

        public void ReleaseGameObject(GameObject game_object)
        {
            lock (this.map_info_lock)
            {
                var x = game_object.current_cell.x;
                var y = game_object.current_cell.y;

                this.map_info[x, y].Remove(game_object);
            }
        }

        public void SetPlayerTargetCell(Player player, Cell cell, DirectionType direction)
        {
            if (direction == DirectionType.TOP_LEFT)
            {
                cell.x += 1;
            }
            else if (direction == DirectionType.TOP_RIGHT)
            {
                cell.x -= 1;
            }
            else if (direction == DirectionType.BOTTOM_LEFT)
            {
                cell.y -= 1;
            }
            else
            {
                cell.y += 1;
            }

            if (player.target_cell.Equals(cell))
            {
                return;
            }

            if (IsOutOfMapRange(cell))
            {
                return;
            }

            lock (this.map_info_lock)
            {
                if (this.map_info[cell.x, cell.y].Any(game_object => game_object is Player))
                {
                    return;
                }

                player.target_cell = cell;
                player.move_timestamp = DateTime.UtcNow;
            }
        }

        bool IsOutOfMapRange(Cell cell)
        {
            return cell.x < 0 || cell.x >= MAP_SIZE || cell.y < 0 || cell.y >= MAP_SIZE;
        }

        public (List<GameObject>, List<long>) GetBoundMapObjectList(
            Cell cell_position,
            ref List<long> bound_player_id_list
        )
        {
            List<GameObject> target_list = new();
            List<long> out_bound_id_list = bound_player_id_list.ConvertAll(s => s);

            lock (this.map_info_lock)
            {
                var min_x = cell_position.x + X_MIN_BOUND;
                var max_x = cell_position.x + X_MAX_BOUND;

                var min_y = cell_position.y + Y_MIN_BOUND;
                var max_y = cell_position.y + Y_MAX_BOUND;

                var line = 0;

                for (int x = min_x; x <= max_x; x++)
                {
                    line += 1;

                    if (line <= 6)
                    {
                        min_y -= 1;
                    }
                    else if (7 < line)
                    {
                        min_y += 1;
                    }

                    if (line <= 20)
                    {
                        max_y += 1;
                    }
                    else if (21 < line)
                    {
                        max_y -= 1;
                    }

                    for (int y = min_y; y <= max_y; y++)
                    {
                        if (IsOutOfMapRange(new Cell(x, y)))
                        {
                            continue;
                        }

                        foreach (GameObject game_object in this.map_info[x, y])
                        {
                            // 기존 인지하지 않던 오브젝트
                            if (!bound_player_id_list.Contains(game_object.object_id))
                            {
                                target_list.Add(game_object);
                                bound_player_id_list.Add(game_object.object_id);
                            }
                            else
                            {
                                // 기존 인지하던 오브젝트
                                if (!game_object.current_cell.Equals(game_object.target_cell))
                                {
                                    // 위치 변경 있으면 추가
                                    target_list.Add(game_object);
                                }
                            }

                            out_bound_id_list.Remove(game_object.object_id);
                        }
                    }
                }

                foreach (int object_id in out_bound_id_list)
                {
                    bound_player_id_list.Remove(object_id);
                }
            }

            return (target_list, out_bound_id_list);
        }
    }
}
