namespace game_server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using StackExchange.Redis;

    public class MapController
    {
        const int MAP_ID = 1;
        const int MAP_SIZE = 100;
        const int X_MIN_BOUND = -11;
        const int X_MAX_BOUND = 14;
        const int Y_MIN_BOUND = -5;
        const int Y_MAX_BOUND = -6;

        string GetMapKey()
        {
            return $"map_{MAP_ID}";
        }

        string GetPositionKey(Cell cell)
        {
            return $"{cell.x},{cell.y}";
        }

        public static string GetBoundObjectKey(long player_id)
        {
            return $"bound_object_{player_id}";
        }

        // async Task<List<GameObjectInfo>> GetGameObject(IDatabase db, List<string> key_list)
        // {
        //     // var split = key.Split("_"); // [object_type, object_id]
        //     // Enum.TryParse(split[0], out ObjectType object_type);
        //     // long.TryParse(split[1], out long object_id);

        //     // string hashes_name = String.Empty;
        //     // switch (object_type)
        //     // {
        //     //     case ObjectType.PLAYER:
        //     //         hashes_name = PlayerInfo.HASH_KEY;
        //     //         break;

        //     //     // case ObjectType.ITEM:
        //     //     //     break;

        //     //     default:
        //     //         break;
        //     // }

        //     List<GameObjectInfo> result = await GameObjectInfo.LoadAll(db, key_list);
        //     return result;
        // }

        async Task<List<string>> GetCellObjectList(Cell cell)
        {
            var serialized = await RedisHelper.HashGet(GetMapKey(), GetPositionKey(cell));
            if (serialized == RedisValue.Null)
            {
                return new();
            }

            var cell_object_list = JsonSerializer.Deserialize<List<string>>(serialized.ToString());
            if (cell_object_list == null)
            {
                throw new Exception($"Deserialize Cell Object List Fail. {serialized}");
            }

            return cell_object_list;
        }

        public async Task SetGameObject(GameObjectInfo game_object)
        {
            var cell_object_list = await GetCellObjectList(game_object.current_cell);
            cell_object_list.Add(game_object.GetHashField());

            await RedisHelper.HashSet(
                GetMapKey(),
                GetPositionKey(game_object.current_cell),
                JsonSerializer.Serialize(cell_object_list)
            );
        }

        public async Task UnsetGameObject(GameObjectInfo game_object)
        {
            var cell_object_list = await GetCellObjectList(game_object.current_cell);
            cell_object_list.Remove(game_object.GetHashField());

            if (cell_object_list.Count <= 0)
            {
                await RedisHelper.HashDelete(GetMapKey(), GetPositionKey(game_object.current_cell));
                return;
            }

            await RedisHelper.HashSet(
                GetMapKey(),
                GetPositionKey(game_object.current_cell),
                JsonSerializer.Serialize(cell_object_list)
            );
        }

        public async Task<GameObjectInfo> MoveGameObject(GameObjectInfo game_object)
        {
            if (game_object.current_cell.Equals(game_object.target_cell))
            {
                return game_object;
            }

            var current_cell_obj_list = await GetCellObjectList(game_object.current_cell);
            current_cell_obj_list.Remove(game_object.GetHashField());

            if (current_cell_obj_list.Count <= 0)
            {
                await RedisHelper.HashDelete(GetMapKey(), GetPositionKey(game_object.current_cell));
            }
            else
            {
                await RedisHelper.HashSet(
                    GetMapKey(),
                    GetPositionKey(game_object.current_cell),
                    JsonSerializer.Serialize(current_cell_obj_list)
                );
            }

            var target_cell_obj_list = await GetCellObjectList(game_object.target_cell);
            target_cell_obj_list.Add(game_object.GetHashField());

            await RedisHelper.HashSet(
                GetMapKey(),
                GetPositionKey(game_object.target_cell),
                JsonSerializer.Serialize(target_cell_obj_list)
            );

            game_object.current_cell = Cell.Clone(game_object.target_cell);
            return game_object;
        }

        public async Task SetPlayerTargetCell(PlayerInfo player, Cell cell, DirectionType direction)
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

            if (player.object_info.target_cell.Equals(cell))
            {
                return;
            }

            if (IsOutOfMapRange(cell))
            {
                return;
            }

            var object_list = await GetCellObjectList(cell);
            foreach (string? game_object_key in object_list)
            {
                if (game_object_key == null)
                {
                    continue;
                }

                var split = game_object_key.Split("_");
                Enum.TryParse(split[0], out ObjectType object_type);
                if (object_type == ObjectType.PLAYER)
                {
                    return;
                }
            }

            player.object_info.target_cell = Cell.Clone(cell);
            player.object_info.move_timestamp = DateTime.UtcNow;
        }

        bool IsOutOfMapRange(Cell cell)
        {
            return cell.x < 0 || cell.x >= MAP_SIZE || cell.y < 0 || cell.y >= MAP_SIZE;
        }

        public async Task<List<string>> GetMapInfoAsync(string key, List<string> field_list)
        {
            try
            {
                var hash_entries = await RedisHelper.HashGet(key, field_list);
                if (hash_entries == null)
                {
                    return new();
                }

                List<string> hash_strings = hash_entries
                    .Where(entry => entry != RedisValue.Null)
                    .Select(entry => entry.ToString())
                    .ToList();

                List<string> result = new();

                for (int i = 0; i < hash_strings.Count; i++)
                {
                    var object_key_list = JsonSerializer.Deserialize<List<string>>(hash_strings[i]);
                    if (object_key_list == null)
                    {
                        continue;
                    }

                    result.AddRange(object_key_list);
                }

                return result;
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.StackTrace}{e.Message}");
                return new();
            }
        }

        public async Task<(List<GameObjectInfo>, List<string>)> GetBoundObjList(PlayerInfo player)
        {
            // string bound_key = GetBoundObjectKey(player.player_id);

            List<GameObjectInfo> target_list = new();

            // var bound_hash = await Program.redis_client.BasicRetryAsync(
            //     (db) => db.HashGetAllAsync(bound_key)
            // );

            // var bound_key_list = bound_hash.Select(entry => entry.Name.ToString()).ToList();
            // List<string> out_bound_key_list = bound_key_list.ConvertAll((s) => s);

            int min_x = player.object_info.target_cell.x + X_MIN_BOUND;
            int max_x = player.object_info.target_cell.x + X_MAX_BOUND;
            int min_y = player.object_info.target_cell.y + Y_MIN_BOUND;
            int max_y = player.object_info.target_cell.y + Y_MAX_BOUND;

            int line = 0;

            // var map_info_hash = await Program.redis_client.BasicRetryAsync(
            //     (db) => db.HashGetAllAsync(GetMapKey())
            // );

            // Dictionary<string, List<string>?> dictionary = new();
            // foreach (HashEntry entry in map_info_hash)
            // {
            //     dictionary[entry.Name] = JsonSerializer.Deserialize<List<string>>(
            //         entry.Value.ToString()
            //     );
            // }

            List<string> position_key_list = new();

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
                    Cell cell = new(x, y);

                    if (IsOutOfMapRange(cell))
                    {
                        continue;
                    }

                    position_key_list.Add(GetPositionKey(cell));
                }
            }

            List<string> object_key_list = await GetMapInfoAsync(GetMapKey(), position_key_list);
            List<GameObjectInfo> game_object_list = await GameObjectInfo.LoadAll(object_key_list);
            target_list.AddRange(game_object_list);

            // var game_object_list = JsonSerializer.Deserialize<List<string>>(serialized);
            // foreach (var object_key in game_object_list)
            // {
            //     if (object_key == null)
            //     {
            //         continue;
            //     }

            //     GameObjectInfo? game_object = await GetGameObject(object_key);
            //     if (game_object == null)
            //     {
            //         continue;
            //     }

            //     // 기존 인지하지 않던 오브젝트
            //     // if (!bound_key_list.Contains(object_key))
            //     {
            //         target_list.Add(game_object);
            //         // Console.WriteLine(
            //         //     $"game_object : {game_object.object_id}, {game_object.current_cell.x}, {game_object.current_cell.y}"
            //         // );
            //         // await Program.redis_client.BasicRetryAsync(
            //         //     (db) =>
            //         //         db.HashSetAsync(
            //         //             bound_key,
            //         //             object_key,
            //         //             JsonSerializer.Serialize(game_object.target_cell)
            //         //         )
            //         // );
            //     }
            //     // 기존 인지하던 오브젝트
            //     // else
            //     // {
            //     //     var serialized = await Program.redis_client.BasicRetryAsync(
            //     //         (db) => db.HashGetAsync(bound_key, object_key)
            //     //     );

            //     //     if (serialized == RedisValue.Null)
            //     //     {
            //     //         continue;
            //     //     }

            //     //     Cell? target_cell = JsonSerializer.Deserialize<Cell>(
            //     //         serialized.ToString()
            //     //     );

            //     //     if (target_cell == null)
            //     //     {
            //     //         continue;
            //     //     }

            //     //     // 위치 변경 있으면 추가
            //     //     if (!game_object.target_cell.Equals(target_cell))
            //     //     {
            //     //         target_list.Add(game_object);
            //     //         await Program.redis_client.BasicRetryAsync(
            //     //             (db) =>
            //     //                 db.HashSetAsync(
            //     //                     bound_key,
            //     //                     object_key,
            //     //                     JsonSerializer.Serialize(game_object.target_cell)
            //     //                 )
            //     //         );
            //     //     }
            //     // }

            //     // out_bound_key_list.Remove(object_key);
            // }

            // // foreach (string object_key in out_bound_key_list)
            // // {
            // //     await Program.redis_client.BasicRetryAsync(
            // //         (db) => db.HashDeleteAsync(bound_key, object_key)
            // //     );
            // // }

            target_list = target_list.GetRange(0, Math.Min(target_list.Count, 20));

            return (target_list, new());
        }

        public Cell GetRandomCell()
        {
            Random random = new();
            return new(random.Next(0, MAP_SIZE), random.Next(0, MAP_SIZE));
        }
    }
}
