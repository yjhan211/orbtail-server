namespace game_server
{
    using System.Collections.Concurrent;
    using MessagePack;
    using network;
    using StackExchange.Redis;
    using user_server;

    public class MapController
    {
        public const int MAP_ID = 1;
        RedisConnection redis_connection;
        ISubscriber? map_publisher;
        public ConcurrentDictionary<string, List<string>> object_position_map;

        public MapController()
        {
            this.redis_connection = RedisConnection.InitializeAsync().GetAwaiter().GetResult();
            this.map_publisher = redis_connection._connection.GetSubscriber();
            this.object_position_map = new();
        }

        public async Task Initialize()
        {
            // 서버 분할 설정
            int horizontal_divisions = 2; // 가로로 2개 섹션
            int vertical_divisions = 5; // 세로로 5개 섹션

            // 각 섹션의 크기 계산
            int section_width = MapHelper.MAP_SIZE / horizontal_divisions;
            int section_height = MapHelper.MAP_SIZE / vertical_divisions;

            // 서버 ID를 기반으로 해당 서버의 가로 세로 위치 계산
            int horizontal_position = (Program.server_id - 1) % horizontal_divisions;
            int vertical_position = (Program.server_id - 1) / horizontal_divisions;

            // 해당 서버가 담당할 맵의 x, y 시작점 계산
            int start_x = horizontal_position * section_width;
            int start_y = vertical_position * section_height;

            // 해당 서버가 담당할 맵의 x, y 끝점 계산
            int end_x = start_x + section_width;
            int end_y = start_y + section_height;

            for (int x = start_x; x < end_x; x++)
            {
                for (int y = start_y; y < end_y; y++)
                {
                    Cell cell = new(x, y);
                    var position_key = MapHelper.GetPositionKey(MAP_ID, cell);
                    this.object_position_map[position_key] = [];
                }
            }

            await this.map_publisher!.SubscribeAsync(
                new($"move_object_{Program.server_id}", RedisChannel.PatternMode.Literal),
                (channel, msg) => MoveManageObject(msg)
            );

            await this.map_publisher!.SubscribeAsync(
                new($"leave_object_{Program.server_id}", RedisChannel.PatternMode.Literal),
                (channel, msg) => LeaveManageObject(msg)
            );

             await this.map_publisher!.SubscribeAsync(
                new($"broadcast_object_{Program.server_id}", RedisChannel.PatternMode.Literal),
                (channel, msg) => BroadcastUpdateObject(msg)
            );
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

object position_lock = new();

        public void MoveManageObject(RedisValue message)
        {
            var (last_position_key, current_position_key, object_info) 
            = MessagePackSerializer.Deserialize<(string, string, GameObjectInfo)>(message);

            // 6. 맵 갱신
            var object_key = GameObjectInfo.MakeHashField(ObjectType.PLAYER, object_info.object_id);

            lock (position_lock)
            {
                // 6-1. last를 관리하는 서버가 본인이면 지움
                if (this.object_position_map.TryGetValue(last_position_key, out _))
                {
                    this.object_position_map[last_position_key].Remove(object_key);
                }

                // 6-2. current 추가
                this.object_position_map[current_position_key].Add(object_key);
            }

            // 7. bound_cell이 포함된 서버에는 브로드캐스트 명령을 보냄
            var bound_cell_list = MapHelper.GetBoundCellList(object_info.current_cell);
            var bound_server_list = bound_cell_list.Select(cell => MapHelper.CalcServerIdFromCell(cell, Program.game_server_num))
                                    .Distinct()
                                    .ToList();

            var broadcast_msg = MessagePackSerializer.Serialize(object_info);                        
            foreach (var server_id in bound_server_list)
            {
                _ = this.map_publisher!.PublishAsync(new($"broadcast_object_{server_id}", RedisChannel.PatternMode.Literal), 
                    broadcast_msg);
            }

            // 9. 마지막 Move요청 처리. 이 처리가 없으면 current_cell과 target_cell이 계속 불일치
            // if (body.direction == DirectionType.NONE)
            // {
            //     return;
            // }

            // _ = Task.Run(async () =>
            // {
            //     await Task.Delay(TimeSpan.FromSeconds(Config.MOVE_ELAPSED_TIME));
            //     await MoveManageObject(cache_helper, player_info, DirectionType.NONE);
            // });
        }

        public void LeaveManageObject(RedisValue message)
        {
            (string position_key, string object_key) = MessagePackSerializer.Deserialize<(string, string)>(message);

            lock (position_lock)
            {
                this.object_position_map[position_key].Remove(object_key);
            }
        }

        public void BroadcastUpdateObject(RedisValue message)
        {
            var object_info = MessagePackSerializer.Deserialize<GameObjectInfo>(message);

            var channels = new List<string>();

            // 담당하는 셀에 영향 받는 유저가 있다면 전송
            foreach (var bound_cell in MapHelper.GetBoundCellList(object_info.current_cell))
            {
                var bound_cell_key = MapHelper.GetPositionKey(MAP_ID, bound_cell);
                if (!this.object_position_map.TryGetValue(bound_cell_key, out var target_user_list))
                {
                    continue;
                }

                if (target_user_list.Count > 0)
                {
                    channels.AddRange(target_user_list);
                }
            }

            if (channels.Count > 0)
            {
                Packet send_packet = PacketMaker.G_TO_U_MOVE(object_info);
                Program.game_server.PublishToChannels(this.map_publisher!, channels, send_packet);
            }
        }
    }
}
