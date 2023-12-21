namespace game_server
{
    using System;
    using System.Collections.Concurrent;
    using MessagePack;
    using StackExchange.Redis;

    public class MapController
    {
        public const int MAP_ID = 1;
        const int MAP_SIZE = 10;
        const int X_MIN_BOUND = -11;
        const int X_MAX_BOUND = 14;
        const int Y_MIN_BOUND = -5;
        const int Y_MAX_BOUND = -6;
        object move_subscribers_lock;
        ConcurrentDictionary<string, ISubscriber> publishers;
        ConcurrentDictionary<string, ISubscriber> subscribers;
        ConcurrentDictionary<string, List<GameUser>> move_subscribe_users;

        public MapController()
        {
            this.move_subscribers_lock = new();

            this.publishers = new();
            this.subscribers = new();
            this.move_subscribe_users = new();
            var cancellationTokenSource = new CancellationTokenSource();

            for (int x = 0; x < MAP_SIZE; x++)
            {
                for (int y = 0; y < MAP_SIZE; y++)
                {
                    Cell cell = new(x, y);
                    var subscriber_key = GetSubscriberKey(cell);
                    var position_key = GetPositionKey(cell);

                    if (!this.subscribers.ContainsKey(subscriber_key))
                    {
                        this.publishers[subscriber_key] =
                            Program.redis_connection._connection.GetSubscriber();

                        this.subscribers[subscriber_key] =
                            Program.redis_connection._connection.GetSubscriber();
                    }
                    this.move_subscribe_users[position_key] = new();

                    this.subscribers[subscriber_key].Subscribe(
                        new(position_key, RedisChannel.PatternMode.Literal),
                        (channel, message) => HandleMoveMessage(channel, message)
                    );
                }
            }
        }

        async Task HandleMoveMessage(RedisChannel channel, RedisValue message)
        {
            var object_info = MessagePackSerializer.Deserialize<GameObjectInfo>(message);
            if (object_info == null)
            {
                return;
            }

            await Task.Run(() =>
            {
                foreach (var subscriber in this.move_subscribe_users[channel!])
                {
                    subscriber.move_queue.Enqueue(object_info);
                }
            });
        }

        public string GetSubscriberKey(Cell cell)
        {
            return $"subscriber_{GetMapKey()}_{(cell.x + cell.y) % 10}";
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
            await player_info.object_info.Save();

            // 4. 구독 타일 변경
            lock (this.move_subscribers_lock)
            {
                this.move_subscribe_users[last_position_key].Remove(user);
                this.move_subscribe_users[current_position_key].Add(user);
            }

            // 새로운 브로드캐스트 영역
            var broadcast_list = GetBoundCellList(player_info.object_info.current_cell);

            foreach (var broadcast_cell in broadcast_list)
            {
                _ = PublishMove(GetPositionKey(broadcast_cell), player_info.object_info);
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
                MessagePackSerializer.Serialize(object_info),
                GetSubscriberKey(object_info.current_cell)
            );
        }

        async Task PublishToChannel(RedisChannel channel, RedisValue message, string publisher_key)
        {
            await this.publishers[publisher_key].PublishAsync(channel, message);
        }
    }
}
