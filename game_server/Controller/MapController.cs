namespace game_server
{
    using System.Collections.Concurrent;
    using MessagePack;
    using network;
    using StackExchange.Redis;
    using user_server;

    public class MapController
    {
        public MapID map_id; // TODO 채널 확장
        ConnectionMultiplexer? redis_connection;
        CacheHelper? cache_helper;
        NatsClient? nats_client;
        public ConcurrentDictionary<string, List<string>> object_position_map;
        public CancellationTokenSource cts;

        public MapController(MapID map_id)
        {
            this.map_id = map_id;
            this.object_position_map = new();
            this.cts = new();
        }

        public void Initialize(NatsClient nats_client)
        {
            this.redis_connection = RedisConnectionPool.GetConnection();
            this.cache_helper = new CacheHelper(redis_connection);

            this.nats_client = nats_client;

            this.nats_client.Subscribe(
                MapHelper.GetMoveManageSubject(this.map_id, Program.server_id),
                (subject, msg) => MoveManageObject(msg)
            );

            this.nats_client.Subscribe(
                MapHelper.GetLeaveManageSubject(this.map_id, Program.server_id),
                (subject, msg) => LeaveManageObject(msg)
            );

            this.nats_client.Subscribe(
                MapHelper.GetSpawnManageSubject(this.map_id, Program.server_id),
                (subject, msg) => SpawnManageObject(msg)
            );

            this.nats_client.Subscribe(
                MapHelper.GetDestroyObjectSubject(this.map_id, Program.server_id),
                (subject, msg) => DestroyManageObject(msg)
            );

            this.nats_client.Subscribe(
                MapHelper.GetUpdatePlayerSubject(this.map_id, Program.server_id),
                (subject, msg) => UpdatePlayerInfo(msg)
            );

            this.nats_client.Subscribe(
                MapHelper.GetBrodcastMoveSubject(this.map_id, Program.server_id),
                (subject, msg) => BroadcastUpdateObject(msg)
            );

            this.nats_client.Subscribe(
                MapHelper.GetBrodcastDestroySubject(this.map_id, Program.server_id),
                (subject, msg) => BroadcastDestroyObject(msg)
            );

            var manage_part_list = MapHelper.GetManagePartList(
                Program.game_server_num,
                Program.server_id
            );

            var manage_position_key_list = new List<string>();

            foreach (var manage_part in manage_part_list)
            {
                manage_position_key_list.AddRange(MapHelper.position_list_by_part[manage_part]);
            }

            foreach (var position_key in manage_position_key_list)
            {
                this.object_position_map[position_key] = new();
            }
        }

        object position_lock = new();

        public void MoveManageObject(RedisValue message)
        {
            var (last_position_key, object_info) = MessagePackSerializer.Deserialize<(
                string,
                GameObjectInfo
            )>(message);

            var object_key = GameObjectInfo.MakeHashField(ObjectType.PLAYER, object_info.object_id);
            var current_position_key = MapHelper.GetPositionKey(
                this.map_id,
                object_info.current_cell
            );

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
            var target_server_list = MapHelper.GetBoundServerList(
                this.map_id,
                Program.game_server_num,
                object_info.current_cell
            );

            foreach (var target_server in target_server_list)
            {
                this.nats_client!.Publish(
                    MapHelper.GetBrodcastMoveSubject(this.map_id, target_server),
                    MessagePackSerializer.Serialize((current_position_key, object_info))
                );
            }
        }

        public int GetCellManagePart(string cell_key)
        {
            MapHelper.part_by_position_key.TryGetValue(cell_key, out var part_id);
            return part_id;
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
            var target_server_list = MapHelper.GetBoundServerList(
                this.map_id,
                Program.game_server_num,
                position_cell
            );

            foreach (var target_server in target_server_list)
            {
                this.nats_client!.Publish(
                    MapHelper.GetBrodcastDestroySubject(this.map_id, target_server),
                    MessagePackSerializer.Serialize((position_key, object_key))
                );
            }
        }

        public void UpdatePlayerInfo(RedisValue message)
        {
            (string position_key, PlayerInfo player_info) = MessagePackSerializer.Deserialize<(
                string,
                PlayerInfo
            )>(message);

            Packet packet = PacketMaker.G_TO_U_PLAYER_INFO(player_info);

            var pivot_cell = MapHelper.GetCell(position_key);
            var bound_cell_list = MapHelper.GetBoundCellList(pivot_cell);

            foreach (var bound_cell in bound_cell_list)
            {
                var bound_position_key = MapHelper.GetPositionKey(this.map_id, bound_cell);
                if (!this.object_position_map.TryGetValue(bound_position_key, out var channel_list))
                {
                    continue;
                }

                if (channel_list.Count <= 0)
                {
                    continue;
                }

                foreach (var channel in channel_list)
                {
                    this.nats_client!.Publish(channel, packet.ToBytes());
                }
            }

            Packet.Destroy(packet);
        }

        public void BroadcastUpdateObject(RedisValue message)
        {
            var (position_key, object_info) = MessagePackSerializer.Deserialize<(
                string,
                GameObjectInfo
            )>(message);

            Packet packet = PacketMaker.G_TO_U_MOVE(object_info);

            var pivot_cell = MapHelper.GetCell(position_key);
            var bound_cell_list = MapHelper.GetBoundCellList(pivot_cell);

            foreach (var bound_cell in bound_cell_list)
            {
                var bound_position_key = MapHelper.GetPositionKey(this.map_id, bound_cell);
                if (!this.object_position_map.TryGetValue(bound_position_key, out var channel_list))
                {
                    continue;
                }

                if (channel_list.Count <= 0)
                {
                    continue;
                }

                foreach (var channel in channel_list)
                {
                    this.nats_client!.Publish(channel, packet.ToBytes());
                }
            }

            Packet.Destroy(packet);
        }

        public void BroadcastDestroyObject(RedisValue message)
        {
            var (position_key, object_key) = MessagePackSerializer.Deserialize<(string, string)>(
                message
            );

            Packet packet = PacketMaker.G_TO_U_DESTROY(object_key);

            var pivot_cell = MapHelper.GetCell(position_key);
            var bound_cell_list = MapHelper.GetBoundCellList(pivot_cell);

            foreach (var bound_cell in bound_cell_list)
            {
                var bound_position_key = MapHelper.GetPositionKey(this.map_id, bound_cell);
                if (!this.object_position_map.TryGetValue(bound_position_key, out var channel_list))
                {
                    continue;
                }

                if (channel_list.Count <= 0)
                {
                    continue;
                }

                foreach (var channel in channel_list)
                {
                    this.nats_client!.Publish(channel, packet.ToBytes());
                }
            }

            Packet.Destroy(packet);
        }
    }
}
