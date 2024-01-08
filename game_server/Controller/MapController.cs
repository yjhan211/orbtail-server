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
        SemaphoreSlim map_publisher_lock;
        public ConcurrentDictionary<Cell, List<string>> object_position_map;
        public CancellationTokenSource cts;
        HashSet<string> collect_position_keys; // 레디스에서 주기적으로 조회하는 셀
        HashSet<string> manage_position_keys; // 조회한 collect_position_key를 publish하는 셀
        Task collect_map_task;
        SemaphoreSlim collect_map_lock;

#pragma warning disable CS8618
        public MapController()
        {
            var redis_connection = RedisConnection.InitializeAsync().GetAwaiter().GetResult();
            this.cache_helper = new(redis_connection);
            this.map_publisher = redis_connection._connection.GetSubscriber();
            this.map_publisher_lock = new(1);

            this.object_position_map = new();

            this.collect_position_keys = new();
            this.manage_position_keys = new();
        }
#pragma warning restore CS8618

        public void Initialize(int server_id)
        {
            int section = MapHelper.MAP_SIZE / Config.GAME_SERVER_NUM;
            int start_x = (server_id - 1) * section;
            int start_y = 0;

            int end_x = server_id * section;
            int end_y = MapHelper.MAP_SIZE;

            for (int x = start_x; x < end_x; x++)
            {
                for (int y = start_y; y < end_y; y++)
                {
                    Cell cell = new(x, y);
                    var manage_position_key = MapHelper.GetPositionKey(MAP_ID, new(cell.x, cell.y));
                    this.manage_position_keys.Add(manage_position_key);

                    foreach (var bound_cell in MapHelper.GetBoundCellList(cell))
                    {
                        var collect_position_key = MapHelper.GetPositionKey(
                            MAP_ID,
                            new(bound_cell.x, bound_cell.y)
                        );
                        this.collect_position_keys.Add(collect_position_key);
                    }
                }
            }

            this.cts = new();
            this.collect_map_lock = new(1);
            this.collect_map_task = Task.Run(CollectMapInfo, cts.Token);
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

                    // 조회 대상인 cell에 있는 유저 키를 모두 조회
                    List<(string key, RedisValue value)> object_keys =
                        await this.cache_helper.ListRangeWithKey(
                            this.collect_position_keys.ToList()
                        );

                    // 서버에 캐싱
                    foreach (var object_key in object_keys)
                    {
                        (string position_key, RedisValue value) = object_key;
                        var cell = MapHelper.GetCell(position_key);
                        this.object_position_map[cell].Add(value.ToString());
                    }

                    this.collect_map_lock.Release();

                    // 조회 대상 유저를 셀 단위로 순회하며 - 대신 관리 대상인 유저들에게만 이 유저들에게 주변 오브젝트 정보를 publish
                    foreach (var object_list in this.object_position_map)
                    {
                        var cell = object_list.Key;
                        var position_key = MapHelper.GetPositionKey(MAP_ID, cell);

                        if (!this.manage_position_keys.Contains(position_key))
                        {
                            continue;
                        }

                        // 셀에 위치한 유저 리스트
                        var channel_list = object_list.Value;

                        // 같은 셀에 위치한 유저들은 같은 주변 오브젝트 정보를 받음
                        var map_info_list = new List<string>();

                        // 가시거리 범위에 있는 셀 순회
                        foreach (var bound_cell in MapHelper.GetBoundCellList(cell))
                        {
                            // 셀을 참조로 앞서 캐싱한 오브젝트들의 키를 넣음
                            map_info_list.AddRange(this.object_position_map[bound_cell]);
                        }

                        if (!map_info_list.Any())
                        {
                            continue;
                        }

                        // 주변 오브젝트 정보를 publish
                        _ = Program.game_server.PublishToChannels(
                            this.map_publisher!,
                            channel_list,
                            PacketMaker.G_TO_U_MAP_INFO(map_info_list)
                        );
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
            await GameObjectController.Save(cache_helper, player_info.object_info);

            var bound_cell_list = MapHelper.GetBoundCellList(player_info.object_info.current_cell);

            await this.collect_map_lock.WaitAsync();

            foreach (var bound_cell in bound_cell_list)
            {
                var target_list = this.object_position_map[bound_cell];
                Packet packet = PacketMaker.G_TO_U_MOVE(player_info.object_info);
                _ = Program.game_server.PublishToChannels(this.map_publisher!, target_list, packet);
            }

            this.collect_map_lock.Release();
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
