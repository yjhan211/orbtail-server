namespace game_server
{
    using System;
    using System.Collections.Concurrent;
    using System.Text.Json;
    using StackExchange.Redis;

    public class MapController
    {
        public const int MAP_ID = 1;
        const int MAP_SIZE = 100;
        const int X_MIN_BOUND = -11;
        const int X_MAX_BOUND = 14;
        const int Y_MIN_BOUND = -5;
        const int Y_MAX_BOUND = -6;
        ConcurrentDictionary<string, List<GameUser>> move_subscribers;
        ISubscriber subscriber;

        public MapController()
        {
            this.subscriber = Program.redis_connection._connection.GetSubscriber();

            this.move_subscribers = new();
            for (int x = 0; x < MAP_SIZE; x++)
            {
                for (int y = 0; y < MAP_SIZE; y++)
                {
                    var position_key = GetPositionKey(new(x, y));

                    this.move_subscribers[position_key] = new();

                    this.subscriber.Subscribe(
                        new(position_key, RedisChannel.PatternMode.Literal),
                        HandleMoveMessage
                    );
                }
            }
        }

        void HandleMoveMessage(RedisChannel channel, RedisValue message)
        {
            var object_info = JsonSerializer.Deserialize<GameObjectInfo?>(message.ToString());
            if (object_info == null)
            {
                return;
            }

            foreach (var subscriber in this.move_subscribers[channel!])
            {
                subscriber.HandleMoveMessage(object_info);
            }
        }

        public string GetMapKey()
        {
            return $"map_{MAP_ID}";
        }

        public static string GetMapKey(int map_id)
        {
            return $"map_{map_id}";
        }

        public string GetPositionKey(Cell cell)
        {
            return $"{GetMapKey()}|{cell.x},{cell.y}";
        }

        public async Task SetPlayer(GameObjectInfo game_object)
        {
            await CacheHelper.ListPush(
                GetPositionKey(game_object.current_cell),
                game_object.GetHashField()
            );
        }

        public async Task UnsetPlayer(GameObjectInfo game_object)
        {
            await CacheHelper.ListRemove(
                GetPositionKey(game_object.current_cell),
                game_object.GetHashField()
            );
        }

        public async Task MovePlayer(GameUser user, DirectionType directon_type)
        {
            PlayerInfo player_info = user.player_info!;

            // 1. current_cell을 target_cell로 변경
            var last_position_key = GetPositionKey(player_info.object_info.current_cell);

            await CacheHelper.ListRemove(last_position_key, player_info.object_info.GetHashField());

            player_info.object_info.current_cell = Cell.Clone(player_info.object_info.target_cell);

            var current_position_key = GetPositionKey(player_info.object_info.current_cell);

            await CacheHelper.ListPush(
                current_position_key,
                player_info.object_info.GetHashField()
            );

            // 2. target_cell을 new_target_cell로 변경 및 move_timestamp 업데이트
            this.SetPlayerTargetCell(
                player_info,
                Cell.Clone(player_info.object_info.current_cell),
                directon_type
            );

            // 3. 변경사항 저장
            await player_info.Save();

            // 4. 구독 타일 변경
            this.move_subscribers[last_position_key].Remove(user);
            this.move_subscribers[current_position_key].Add(user);

            // 새로운 브로드캐스트 영역
            var broadcast_list = GetBoundCellList(player_info.object_info.current_cell);

            foreach (var broadcast_cell in broadcast_list)
            {
                await PublishMove(GetPositionKey(broadcast_cell), player_info.object_info);
            }
        }

        void SetPlayerTargetCell(PlayerInfo player, Cell cell, DirectionType direction)
        {
            switch (direction)
            {
                case DirectionType.TOP_LEFT:
                    cell.x += 1;
                    break;

                case DirectionType.TOP_RIGHT:
                    cell.x -= 1;
                    break;

                case DirectionType.BOTTOM_LEFT:
                    cell.y -= 1;
                    break;

                case DirectionType.BOTTOM_RIGHT:
                    cell.y += 1;
                    break;

                default:
                    return;
            }

            player.object_info.move_timestamp = DateTime.UtcNow;

            if (IsOutOfMapRange(cell))
            {
                return;
            }

            player.object_info.target_cell = cell;
            player.object_info.SetFlip(direction);

            return;
        }

        static bool IsOutOfMapRange(Cell cell)
        {
            return cell.x < 0 || cell.x >= MAP_SIZE || cell.y < 0 || cell.y >= MAP_SIZE;
        }

        public static List<Cell> GetBoundCellList(Cell pivot_cell)
        {
            List<Cell> result = new();

            int min_x = pivot_cell.x + X_MIN_BOUND;
            int max_x = pivot_cell.x + X_MAX_BOUND;
            int min_y = pivot_cell.y + Y_MIN_BOUND;
            int max_y = pivot_cell.y + Y_MAX_BOUND;

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

                    result.Add(cell);
                }
            }

            return result;
        }

        public Cell GetRandomCell()
        {
            Random random = new();
            return new(random.Next(0, MAP_SIZE), random.Next(0, MAP_SIZE));
        }

        public async Task PublishMove(string position_key, GameObjectInfo object_info)
        {
            await PublishToChannel(
                new(position_key, RedisChannel.PatternMode.Literal),
                JsonSerializer.Serialize(object_info)
            );
        }

        async Task PublishToChannel(RedisChannel channel, RedisValue message)
        {
            await this.subscriber.PublishAsync(channel, message);
        }
    }
}
