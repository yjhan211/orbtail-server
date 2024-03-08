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
        ConnectionMultiplexer? redis_connection;
        CacheHelper? cache_helper;
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
            this.redis_connection = RedisConnectionPool.GetConnection();
            this.cache_helper = new CacheHelper(redis_connection);

            this.nats_client = nats_client;

            var manage_part_list = MapHelper.GetManagePartList(
                Program.game_server_num,
                Program.server_id
            );

            var manage_position_key_list = new List<string>();
            foreach (var manage_part in manage_part_list)
            {
                this.nats_client.Subscribe(
                    $"move_object_{manage_part}",
                    (subject, msg) => MoveManageObject(msg)
                );

                this.nats_client.Subscribe(
                    $"leave_object_{manage_part}",
                    (subject, msg) => LeaveManageObject(msg)
                );

                this.nats_client.Subscribe(
                    $"spawn_object_{manage_part}",
                    (subject, msg) => SpawnManageObject(msg)
                );

                this.nats_client.Subscribe(
                    $"destroy_object_{manage_part}",
                    (subject, msg) => DestroyManageObject(msg)
                );

                manage_position_key_list.AddRange(MapHelper.position_list_by_part[manage_part]);
            }

            foreach (var position_key in manage_position_key_list)
            {
                this.object_position_map[position_key] = new();

                this.nats_client.Subscribe(
                    $"update_{position_key}",
                    (subject, msg) => BroadcastUpdateObject(position_key, msg)
                );

                this.nats_client.Subscribe(
                    $"destroy_{position_key}",
                    (subject, msg) => BroadcastDestroyObject(position_key, msg)
                );
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
                var subject = $"update_{MapHelper.GetPositionKey(bound_cell)}";
                this.nats_client!.Publish(subject, broadcast_msg);
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

        public void DestroyManageObject(RedisValue message)
        {
            (string position_key, string object_key) = MessagePackSerializer.Deserialize<(
                string,
                string
            )>(message);

            lock (position_lock)
            {
                this.object_position_map[position_key].Remove(object_key);
            }

            Cell position_cell = MapHelper.GetCell(position_key);
            var bound_cell_list = MapHelper.GetBoundCellList(position_cell);

            foreach (var bound_cell in bound_cell_list)
            {
                var bound_cell_key = MapHelper.GetPositionKey(bound_cell);
                this.nats_client!.Publish(
                    $"destroy_{bound_cell_key}",
                    MessagePackSerializer.Serialize(object_key)
                );
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

        public void BroadcastDestroyObject(string position_key, RedisValue message)
        {
            var object_key = MessagePackSerializer.Deserialize<string>(message);
            if (!this.object_position_map.TryGetValue(position_key, out var channel_list))
            {
                return;
            }

            if (channel_list.Count <= 0)
            {
                return;
            }

            Packet packet = PacketMaker.G_TO_U_DESTROY(object_key);

            foreach (var channel in channel_list)
            {
                this.nats_client!.Publish(channel, packet.ToBytes());
            }

            Packet.Destroy(packet);
        }
    }
}
