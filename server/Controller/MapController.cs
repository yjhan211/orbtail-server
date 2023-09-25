namespace game_server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
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

        string GetMapPositionKey(Cell cell)
        {
            return $"map_{MAP_ID}({cell.x},{cell.y})";
        }

        public static string GetBoundObjectKey(long player_id)
        {
            return $"bound_object_{player_id}";
        }

        public static string GetGameObjectKey(GameObjectInfo game_object)
        {
            return $"{game_object.object_type}_{game_object.object_id}";
        }

        async Task<GameObjectInfo?> GetGameObject(string key)
        {
            var split = key.Split("_"); // [object_type, object_id]
            Enum.TryParse(split[0], out ObjectType object_type);
            long.TryParse(split[1], out long object_id);

            string hashes_name = String.Empty;
            switch (object_type)
            {
                case ObjectType.PLAYER:
                    hashes_name = PlayerInfo.HASH_KEY;
                    break;

                // case ObjectType.ITEM:
                //     break;

                default:
                    break;
            }

            GameObjectInfo? game_object = await GameObjectInfo.Load(object_type, object_id);
            return game_object;
        }

        public async void SpawnGameObject(GameObjectInfo game_object)
        {
            var current_cell = game_object.current_cell;
            await Program.redis_client.BasicRetryAsync(
                (db) =>
                    db.SetAddAsync(GetMapPositionKey(current_cell), GetGameObjectKey(game_object))
            );
        }

        public async Task<GameObjectInfo> MoveGameObject(GameObjectInfo game_object)
        {
            if (game_object.current_cell.Equals(game_object.target_cell))
            {
                return game_object;
            }

            await Program.redis_client.BasicRetryAsync(
                (db) =>
                    db.SetRemoveAsync(
                        GetMapPositionKey(game_object.current_cell),
                        GetGameObjectKey(game_object)
                    )
            );

            await Program.redis_client.BasicRetryAsync(
                (db) =>
                    db.SetAddAsync(
                        GetMapPositionKey(game_object.target_cell),
                        GetGameObjectKey(game_object)
                    )
            );

            game_object.current_cell = Cell.Clone(game_object.target_cell);

            return game_object;
        }

        public async Task ReleaseGameObject(GameObjectInfo game_object)
        {
            var map_position_key = GetMapPositionKey(game_object.current_cell);
            var game_object_key = GetGameObjectKey(game_object);
            await Program.redis_client.BasicRetryAsync(
                (db) => db.SetRemoveAsync(map_position_key, game_object_key)
            );

            var bound_object_key = GetBoundObjectKey(game_object.object_id);
            await Program.redis_client.BasicRetryAsync((db) => db.KeyDeleteAsync(bound_object_key));
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

            var object_list = await Program.redis_client.BasicRetryAsync(
                (db) => db.SetMembersAsync(GetMapPositionKey(cell))
            );

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

        public async Task<(List<GameObjectInfo>, List<string>)> GetBoundObjList(PlayerInfo player)
        {
            string bound_key = GetBoundObjectKey(player.player_id);

            List<GameObjectInfo> target_list = new();

            var bound_hash = await Program.redis_client.BasicRetryAsync(
                (db) => db.HashGetAllAsync(bound_key)
            );

            var bound_key_list = bound_hash.Select(entry => entry.Name.ToString()).ToList();
            List<string> out_bound_key_list = bound_key_list.ConvertAll((s) => s);

            int min_x = player.object_info.target_cell.x + X_MIN_BOUND;
            int max_x = player.object_info.target_cell.x + X_MAX_BOUND;
            int min_y = player.object_info.target_cell.y + Y_MIN_BOUND;
            int max_y = player.object_info.target_cell.y + Y_MAX_BOUND;

            int line = 0;
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

                    var game_object_hash = await Program.redis_client.BasicRetryAsync(
                        (db) => db.SetMembersAsync(GetMapPositionKey(cell))
                    );

                    foreach (string? object_key in game_object_hash.ToList())
                    {
                        if (object_key == null)
                        {
                            continue;
                        }

                        GameObjectInfo? game_object = await GetGameObject(object_key);
                        if (game_object == null)
                        {
                            continue;
                        }

                        // 기존 인지하지 않던 오브젝트
                        if (!bound_key_list.Contains(object_key))
                        {
                            target_list.Add(game_object);
                            await Program.redis_client.BasicRetryAsync(
                                (db) =>
                                    db.HashSetAsync(
                                        bound_key,
                                        object_key,
                                        JsonSerializer.Serialize(game_object.target_cell)
                                    )
                            );
                        }
                        // 기존 인지하던 오브젝트
                        else
                        {
                            var serialized = await Program.redis_client.BasicRetryAsync(
                                (db) => db.HashGetAsync(bound_key, object_key)
                            );

                            if (serialized == RedisValue.Null)
                            {
                                continue;
                            }

                            Cell? target_cell = JsonSerializer.Deserialize<Cell>(
                                serialized.ToString()
                            );

                            if (target_cell == null)
                            {
                                continue;
                            }

                            // 위치 변경 있으면 추가
                            if (!game_object.target_cell.Equals(target_cell))
                            {
                                target_list.Add(game_object);
                                await Program.redis_client.BasicRetryAsync(
                                    (db) =>
                                        db.HashSetAsync(
                                            bound_key,
                                            object_key,
                                            JsonSerializer.Serialize(game_object.target_cell)
                                        )
                                );
                            }
                        }

                        out_bound_key_list.Remove(object_key);
                    }
                }
            }

            foreach (string object_key in out_bound_key_list)
            {
                await Program.redis_client.BasicRetryAsync(
                    (db) => db.HashDeleteAsync(bound_key, object_key)
                );
            }

            return (target_list, out_bound_key_list);
        }

        public Cell GetRandomCell()
        {
            Random random = new();
            return new(random.Next(0, MAP_SIZE), random.Next(0, MAP_SIZE));
        }
    }
}
