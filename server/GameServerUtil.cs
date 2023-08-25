namespace game_server
{
    using System;
    using network;

    public partial class GameServer
    {
        public List<GameObject> GetBoundGameObjectList(CellPosition cell_position)
        {
            List<GameObject> target_list = new();

            const int X_MIN_BOUND = -11;
            const int X_MAX_BOUND = 14;

            const int Y_MIN_BOUND = -5;
            const int Y_MAX_BOUND = -6;

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

                    foreach (List<GameObject> game_object_list in this.world_info)
                    {
                        target_list.AddRange(game_object_list);
                    }
                }
            }

            return target_list;
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

        void MoveFinish(GameObject game_object, CellPosition target_cell)
        {
            if (game_object.current_cell.Equals(target_cell))
            {
                return;
            }

            this.world_info[game_object.current_cell.x, game_object.current_cell.y].Remove(
                game_object
            );

            this.world_info[target_cell.x, target_cell.y].Add(game_object);
            game_object.current_cell = target_cell;
        }

        bool RemoveInMap(GameObject game_object)
        {
            this.world_info[game_object.current_cell.x, game_object.current_cell.y].Remove(
                game_object
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
            for (int x = MAP_SIZE - 1; x >= 0; x--)
            {
                for (int y = MAP_SIZE - 1; y >= 0; y--)
                {
                    if (this.world_info[x, y].Count == 0)
                    {
                        Console.Write("X,");
                    }
                    else
                    {
                        Console.Write("O,");
                    }
                }
                Console.WriteLine("");
            }

            Console.WriteLine("==========================================");
        }
    }
}
