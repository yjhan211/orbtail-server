namespace game_server
{
    using System.Collections.Concurrent;
    using MessagePack;
    using network;
    using StackExchange.Redis;
    using user_server;
    using NATS.Client;

    public class MapController
    {
        public const int MAP_ID = 1;
        NatsClient? nats_client;
        public ConcurrentDictionary<string, List<string>> object_position_map;
        public CancellationTokenSource cts;

        public MapController()
        {
            this.object_position_map = new();
            this.cts = new();
        }

        public void Initialize(NatsClient nats_client)
        {
            this.nats_client = nats_client;

            // 서버 분할 설정
            var (horizontal_divisions, vertical_divisions) = MapHelper.DetermineDivisions(
                Program.game_server_num
            );
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
                    this.object_position_map[position_key] = new();

                    this.nats_client.Subscribe(
                        position_key,
                        (subject, msg) => BroadcastUpdateObject(subject, msg)
                    );

                    foreach (var bound_cell in MapHelper.GetBoundCellList(cell))
                    {
                        var bound_cell_key = MapHelper.GetPositionKey(bound_cell);
                    }
                }
            }

            this.nats_client.Subscribe(
                $"move_object_{Program.server_id}",
                (subject, msg) => MoveManageObject(msg)
            );

            this.nats_client.Subscribe(
                $"leave_object_{Program.server_id}",
                (subject, msg) => LeaveManageObject(msg)
            );

            this.nats_client.Subscribe(
                $"spawn_object_{Program.server_id}",
                (subject, msg) => SpawnManageObject(msg)
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
            var (last_position_key, object_info) = MessagePackSerializer.Deserialize<(
                string,
                GameObjectInfo
            )>(message);

            var object_key = GameObjectInfo.MakeHashField(ObjectType.PLAYER, object_info.object_id);
            var current_position_key = MapHelper.GetPositionKey(object_info.current_cell);

            // 위치 갱신
            lock (position_lock)
            {
                // last를 관리하는 서버가 본인이면 지움
                if (this.object_position_map.TryGetValue(last_position_key, out _))
                {
                    this.object_position_map[last_position_key].Remove(object_key);
                }

                // current 추가
                this.object_position_map[current_position_key].Add(object_key);
            }

            // bound_cell이 포함된 서버에는 브로드캐스트 명령을 보냄
            var bound_cell_list = MapHelper.GetBoundCellList(object_info.current_cell);
            var broadcast_msg = MessagePackSerializer.Serialize(object_info);
            foreach (var bound_cell in bound_cell_list)
            {
                this.nats_client!.Publish(MapHelper.GetPositionKey(bound_cell), broadcast_msg);
            }
        }

        public void LeaveManageObject(RedisValue message)
        {
            (string position_key, string object_key) = MessagePackSerializer.Deserialize<(
                string,
                string
            )>(message);

            lock (position_lock)
            {
                this.object_position_map[position_key].Remove(object_key);
            }
        }

        public void SpawnManageObject(RedisValue message)
        {
            (string user_subject, List<string> position_key_list) =
                MessagePackSerializer.Deserialize<(string, List<string>)>(message);

            var spawn_list = new List<string>();
            foreach (var position_key in position_key_list)
            {
                spawn_list.AddRange(this.object_position_map[position_key]);
            }

            if (spawn_list.Count > 0)
            {
                Packet packet = PacketMaker.G_TO_U_SPAWN(spawn_list);
                this.nats_client!.Publish(user_subject, packet.ToBytes());
                Packet.Destroy(packet);
            }
        }

        public void BroadcastUpdateObject(string position_key, RedisValue message)
        {
            var object_info = MessagePackSerializer.Deserialize<GameObjectInfo>(message);
            if (!this.object_position_map.TryGetValue(position_key, out var channel_list))
            {
                return;
            }

            if (channel_list.Count <= 0)
            {
                return;
            }

            Packet packet = PacketMaker.G_TO_U_MOVE(object_info);
            foreach (var channel in channel_list)
            {
                this.nats_client!.Publish(channel, packet.ToBytes());
            }
            Packet.Destroy(packet);
        }
    }
}
