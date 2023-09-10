namespace game_server
{
    using System;
    using network;

    public partial class GameServer
    {
        public (List<GameObject>, List<long>) GetBoundMapObjectList(
            CellPosition cell_position,
            ref List<long> bound_player_id_list
        )
        {
            const int X_MIN_BOUND = -11;
            const int X_MAX_BOUND = 14;

            const int Y_MIN_BOUND = -5;
            const int Y_MAX_BOUND = -6;

            List<GameObject> target_list = new();
            List<long> out_bound_id_list = bound_player_id_list.ConvertAll(s => s);

            lock (this.world_lock)
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
                        if (IsOutOfMapRange(new CellPosition(x, y)))
                        {
                            continue;
                        }

                        for (int i = 0; i < this.world_info[x, y].Count; i++)
                        {
                            // 기존 인지하지 않던 오브젝트
                            if (!bound_player_id_list.Contains(this.world_info[x, y][i].object_id))
                            {
                                target_list.Add(this.world_info[x, y][i]);
                                bound_player_id_list.Add(this.world_info[x, y][i].object_id);
                            }
                            else
                            {
                                // 기존 인지하던 오브젝트
                                if (
                                    // 위치 변경 없으면 스킵
                                    this.world_info[x, y][i].current_cell.Equals(
                                        this.world_info[x, y][i].target_cell
                                    )
                                )
                                {
                                    continue;
                                }
                                else
                                {
                                    // 위치 변경 있으면 추가
                                    target_list.Add(this.world_info[x, y][i]);
                                }
                            }

                            out_bound_id_list.Remove(this.world_info[x, y][i].object_id);
                        }

                        // target_list.AddRange(this.world_info[x, y]);
                    }
                }
            }

            // Console.WriteLine(target_list.Count);
            return (target_list, out_bound_id_list);
        }

        (CellPosition?, bool) CalcMoveTarget(CellPosition current_cell, DirectionType direction)
        {
            bool is_flip = false;

            if (direction == DirectionType.TOP_LEFT)
            {
                current_cell.x += 1;
                is_flip = true;
            }
            else if (direction == DirectionType.TOP_RIGHT)
            {
                current_cell.x -= 1;
            }
            else if (direction == DirectionType.BOTTOM_LEFT)
            {
                current_cell.y -= 1;
                is_flip = true;
            }
            else
            {
                current_cell.y += 1;
            }

            if (IsOutOfMapRange(current_cell))
            {
                return (null, is_flip);
            }

            if (HasPlayer(current_cell))
            {
                return (null, is_flip);
            }

            return (current_cell, is_flip);
        }

        void MoveFinish(GameObject map_object, CellPosition target_cell)
        {
            if (map_object.current_cell.Equals(target_cell))
            {
                return;
            }

            this.world_info[map_object.current_cell.x, map_object.current_cell.y].Remove(
                map_object
            );

            this.world_info[target_cell.x, target_cell.y].Add(map_object);
            map_object.current_cell = target_cell;

            DrawPlayerCellInfo();
        }

        bool RemoveInMap(GameObject map_object)
        {
            this.world_info[map_object.current_cell.x, map_object.current_cell.y].Remove(
                map_object
            );

            return true;
        }

        bool HasPlayer(CellPosition cell_position)
        {
            foreach (GameObject game_object in this.world_info[cell_position.x, cell_position.y])
            {
                if (game_object is Player)
                {
                    return true;
                }
            }

            return false;
        }

        bool IsOutOfMapRange(CellPosition cell)
        {
            return cell.x < 0 || cell.x >= MAP_SIZE || cell.y < 0 || cell.y >= MAP_SIZE;
        }

        double GetMoveElapsedTime(Player player)
        {
            TimeSpan elapsedTime = DateTime.UtcNow - player.move_timestamp;
            return elapsedTime.TotalSeconds;
        }

        public void DrawPlayerCellInfo()
        {
            return;

            for (int x = MAP_SIZE - 1; x >= 0; x--)
            {
                for (int y = MAP_SIZE - 1; y >= 0; y--)
                {
                    Console.Write($"{this.world_info[x, y].Count}");
                }
                Console.WriteLine("");
            }

            Console.WriteLine("==========================================");
        }
    }
}
