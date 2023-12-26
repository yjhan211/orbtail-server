namespace game_server
{
    using System;
    using System.Collections.Concurrent;
    using MessagePack;
    using network;
    using StackExchange.Redis;

    public class MapController
    {
        public const int MAP_ID = 1;
        object move_subscribers_lock;
        ConcurrentDictionary<string, ISubscriber> subscribers;
        ConcurrentDictionary<string, List<long>> move_subscribe_users;

        public MapController()
        {
            this.move_subscribers_lock = new();

            this.subscribers = new();
            this.move_subscribe_users = new();
            var cancellationTokenSource = new CancellationTokenSource();

            for (int x = 0; x < MapHelper.MAP_SIZE; x++)
            {
                for (int y = 0; y < MapHelper.MAP_SIZE; y++)
                {
                    Cell cell = new(x, y);
                    var subscriber_key = GetSubscriberKey(cell);
                    var position_key = GetPositionKey(cell);

                    if (!this.subscribers.ContainsKey(subscriber_key))
                    {
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
                foreach (long player_id in this.move_subscribe_users[channel!])
                {
                    Program.game_server.publisher.PublishAsync(
                        new($"object_{player_id}", RedisChannel.PatternMode.Literal),
                        MessagePackSerializer.Serialize(object_info)
                    );
                    // subscriber.move_queue.Enqueue(object_info);
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
            await Program.cache_helper.ListPush(
                GetPositionKey(game_object.current_cell),
                game_object.GetHashField()
            );
        }

        public async Task UnsetPlayer(GameObjectInfo game_object)
        {
            await Program.cache_helper.ListRemove(
                GetPositionKey(game_object.current_cell),
                game_object.GetHashField()
            );
        }

        public async Task MovePlayer(PlayerInfo player_info, DirectionType directon_type)
        {
            // 1. current_cell을 target_cell로 변경
            var last_position_key = GetPositionKey(player_info.object_info.current_cell);

            await Program.cache_helper.ListRemove(
                last_position_key,
                player_info.object_info.GetHashField()
            );

            player_info.object_info.current_cell = Cell.Clone(player_info.object_info.target_cell);

            var current_position_key = GetPositionKey(player_info.object_info.current_cell);

            await Program.cache_helper.ListPush(
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
            await GameObjecController.Save(player_info.object_info);

            // 4. 구독 타일 변경
            lock (this.move_subscribers_lock)
            {
                this.move_subscribe_users[last_position_key].Remove(player_info.player_id);
                this.move_subscribe_users[current_position_key].Add(player_info.player_id);
            }

            // 새로운 브로드캐스트 영역
            var broadcast_list = MapHelper.GetBoundCellList(player_info.object_info.current_cell);

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

            if (MapHelper.IsOutOfMapRange(cell))
            {
                return;
            }

            player.object_info.target_cell = cell;
            player.object_info.SetFlip(direction);

            return;
        }

        public async Task PublishMove(string position_key, GameObjectInfo object_info)
        {
            await Program.game_server.PublishToChannel(
                position_key,
                MessagePackSerializer.Serialize(object_info)
            );
        }
    }
}
