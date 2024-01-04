namespace game_server
{
    using System;
    using System.Collections.Concurrent;
    using MessagePack;
    using network;
    using StackExchange.Redis;
    using user_server;

    public class MapController
    {
        public const int MAP_ID = 1;
        CacheHelper cache_helper;
        ISubscriber? map_publisher;
        public ConcurrentDictionary<Cell, List<string>> object_position_map;
        public CancellationTokenSource cts;
        List<string> position_key_all;
        Task collect_map_task;
        SemaphoreSlim collect_map_lock;

        public MapController()
        {
            var redis_connection = RedisConnection.InitializeAsync().GetAwaiter().GetResult();
            this.cache_helper = new(redis_connection);
            this.map_publisher = redis_connection._connection.GetSubscriber();

            this.object_position_map = new();

            this.cts = new();
            this.collect_map_task = Task.Run(CollectMapInfo, cts.Token);
            this.collect_map_lock = new(1);

            this.position_key_all = new();
        }

        public void Initialize()
        {
            for (int x = 0; x < MapHelper.MAP_SIZE; x++)
            {
                for (int y = 0; y < MapHelper.MAP_SIZE; y++)
                {
                    this.position_key_all.Add(MapHelper.GetPositionKey(MAP_ID, new(x, y)));
                }
            }

            InitPositionMap();
        }

        void InitPositionMap()
        {
            for (int x = 0; x < MapHelper.MAP_SIZE; x++)
            {
                for (int y = 0; y < MapHelper.MAP_SIZE; y++)
                {
                    Cell cell = new(x, y);
                    this.object_position_map[cell] = new();
                }
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

        async Task CollectMapInfo()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await this.collect_map_lock.WaitAsync();

                    InitPositionMap();

                    List<(string key, RedisValue value)> object_keys =
                        await this.cache_helper.ListRangeWithKey(this.position_key_all);

                    foreach (var object_key in object_keys)
                    {
                        (string position_key, RedisValue value) = object_key;
                        var cell = MapHelper.GetCell(position_key);
                        this.object_position_map[cell].Add(value.ToString());

                        Console.WriteLine(
                            $"2 {cell.x}, {cell.y} | count: {this.object_position_map[cell].Count}"
                        );
                    }

                    // TODO 여러개의 서버 인스턴스가 나눠서 다루도록 수정해야 함
                    foreach (var object_list in this.object_position_map)
                    {
                        var position_key = object_list.Key;
                        var channel_list = object_list.Value;

                        var map_info_list = new List<string>();
                        foreach (var bound_cell in MapHelper.GetBoundCellList(position_key))
                        {
                            map_info_list.AddRange(this.object_position_map[bound_cell]);
                        }

                        if (!map_info_list.Any())
                        {
                            continue;
                        }

                        Packet packet = PacketMaker.G_TO_U_MAP_INFO(map_info_list);
                        foreach (var channel in channel_list)
                        {
                            _ = Program.game_server.PublishToChannel(
                                this.map_publisher!,
                                channel,
                                packet
                            );
                        }
                    }

                    await Task.Delay(1000);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[UserServer] {e.StackTrace} || {e.Message}");
                }
                finally
                {
                    this.collect_map_lock.Release();
                }
            }

            try
            {
                this.collect_map_task!.Wait();
                this.collect_map_lock.Release();
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

            var bound_cell_list = MapHelper.GetBoundCellList(player_info.object_info.current_cell);
            foreach (var bound_cell in bound_cell_list)
            {
                var target_list = this.object_position_map[bound_cell];
                Packet packet = PacketMaker.G_TO_U_MOVE(player_info.object_info);

                foreach (var target in target_list)
                {
                    _ = Program.game_server.PublishToChannel(this.map_publisher!, target, packet);
                }
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
    }
}
