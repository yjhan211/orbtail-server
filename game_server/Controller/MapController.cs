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
        readonly object map_subscribers_lock;
        CacheHelper cache_helper;
        ISubscriber? map_publisher;
        ConcurrentDictionary<string, ISubscriber> subscribers;
        ConcurrentDictionary<string, List<long>> move_subscribe_users;
        public List<string>[,] object_map;
        public CancellationTokenSource cts;
        List<string> position_key_all;
        Task map_info_task;

        public MapController()
        {
            this.map_subscribers_lock = new();
            this.subscribers = new();
            this.move_subscribe_users = new();

            var redis_connection = RedisConnection.InitializeAsync().GetAwaiter().GetResult();
            this.cache_helper = new(redis_connection);
            this.map_publisher = redis_connection._connection.GetSubscriber();

            this.object_map = new List<string>[MapHelper.MAP_SIZE, MapHelper.MAP_SIZE];

            this.cts = new();
            this.map_info_task = Task.Run(MapInfoTask, cts.Token);

            this.position_key_all = new();
        }

        public async Task InitializeAsync()
        {
            for (int x = 0; x < MapHelper.MAP_SIZE; x++)
            {
                for (int y = 0; y < MapHelper.MAP_SIZE; y++)
                {
                    Cell cell = new(x, y);
                    var subscriber_key = GetSubscriberKey(cell);
                    var position_key = GetPositionKey(cell);

                    if (!this.subscribers.ContainsKey(subscriber_key))
                    {
                        var redis_conn = await RedisConnection.InitializeAsync();
                        this.subscribers[subscriber_key] = redis_conn._connection.GetSubscriber();
                    }
                    this.move_subscribe_users[position_key] = new();

#pragma warning disable CS4014
                    this.subscribers[subscriber_key].Subscribe(
                        new(position_key, RedisChannel.PatternMode.Literal),
                        (channel, message) => HandleMoveMessage(channel, message)
                    );
#pragma warning restore CS4014

                    this.object_map[x, y] = new();
                    this.position_key_all.Add(MapHelper.GetPositionKey(MAP_ID, cell));
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

            var redis_conn = await RedisConnection.InitializeAsync();
            map_publisher = redis_conn._connection.GetSubscriber();

            foreach (long player_id in this.move_subscribe_users[channel!])
            {
                _ = Program.game_server.PublishToChannel(
                    this.map_publisher,
                    $"object_{player_id}",
                    MessagePackSerializer.Serialize(object_info)
                );
            }
        }

        public string GetSubscriberKey(Cell cell)
        {
            return $"subscriber_{GetMapKey()}_{cell.x % 10}";
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

        async Task MapInfoTask()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    List<(string key, RedisValue value)> object_keys =
                        await this.cache_helper.ListRangeWithKey(this.position_key_all);

                    for (int i = 0; i < object_keys.Count; i++)
                    {
                        (string key, RedisValue value) = object_keys[i];
                        var cell = MapHelper.GetCell(key);
                        this.object_map[cell.x, cell.y].Add(value.ToString());
                    }

                    await Task.Delay(1000);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[UserServer] {e.StackTrace} || {e.Message}");
                }
            }

            try
            {
                this.map_info_task!.Wait();
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"[UserServer] {e.StackTrace} || {e.Message}");
            }
        }

        public async Task MovePlayer(
            CacheHelper cache_helper,
            PlayerInfo player_info,
            DirectionType directon_type
        )
        {
            // 1. current_cell을 target_cell로 변경
            var last_position_key = GetPositionKey(player_info.object_info.current_cell);

            await cache_helper.ListRemove(
                last_position_key,
                player_info.object_info.GetHashField()
            );

            player_info.object_info.current_cell = Cell.Clone(player_info.object_info.target_cell);

            var current_position_key = GetPositionKey(player_info.object_info.current_cell);

            await cache_helper.ListPush(
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
            await GameObjecController.Save(cache_helper, player_info.object_info);

            // 4. 구독 타일 변경
            lock (this.map_subscribers_lock)
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
                this.map_publisher!,
                position_key,
                MessagePackSerializer.Serialize(object_info)
            );
        }
    }
}
