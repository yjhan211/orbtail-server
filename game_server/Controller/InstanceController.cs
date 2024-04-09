namespace game_server
{
    using System.Collections.Concurrent;
    using MessagePack;
    using network;
    using StackExchange.Redis;
    using user_server;

    public class InstanceController
    {
        ConnectionMultiplexer? redis_connection;
        CacheHelper? cache_helper;
        NatsClient? nats_client;

        ConcurrentDictionary<string, List<string>> object_instacne_dict; // Instance_id, [object_key, ..]

        public InstanceController()
        {
            this.object_instacne_dict = new();
        }

        public void Initialize(NatsClient nats_client)
        {
            this.redis_connection = RedisConnectionPool.GetConnection();
            this.cache_helper = new CacheHelper(redis_connection);
            this.nats_client = nats_client;

            this.nats_client.Subscribe(
                MapHelper.GetCreateInstanceSubject(Program.server_id),
                (subject, msg) =>
                {
                    try
                    {
                        CreateInstance(msg);
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                }
            );
        }

        void CreateInstance(RedisValue message)
        {
            var (user_subject, map_id, map_sub_id) = MessagePackSerializer.Deserialize<(
                string,
                MapID,
                long
            )>(message);

            switch (map_id)
            {
                case MapID.LAB_1:
                    var instance_key = MapHelper.GetInstanceKey(map_id, map_sub_id);
                    if (!this.object_instacne_dict.TryGetValue(instance_key, out var _))
                    {
                        this.object_instacne_dict[instance_key] = new();
                        SubscribeInstance(map_id, map_sub_id);
                    }
                    break;

                default:
                    throw new Exception("inavlid map id");
            }

            Packet packet = PacketMaker.G_TO_U_CREATE_INSTANCE_SUCCESS(map_id, map_sub_id);
            this.nats_client!.Publish(user_subject, packet.ToBytes());

            Packet.Destroy(packet);
        }

        object position_lock = new();

        public void MoveManageObject(RedisValue message)
        {
            var (last_position_key, object_info) = MessagePackSerializer.Deserialize<(
                string,
                GameObjectInfo
            )>(message);

            var object_key = GameObjectInfo.MakeHashField(ObjectType.PLAYER, object_info.object_id);
            var last_instance_key = MapHelper.ConvertToInstanceKey(last_position_key);
            var current_instance_key = MapHelper.GetInstanceKey(
                object_info.map_id,
                object_info.map_sub_id
            );

            // 위치 갱신
            lock (position_lock)
            {
                // last를 관리하는 서버가 본인이면 지움
                if (this.object_instacne_dict.TryGetValue(last_instance_key, out _))
                {
                    this.object_instacne_dict[last_instance_key].Remove(object_key);
                }

                // current 추가
                this.object_instacne_dict[current_instance_key].Add(object_key);
            }

            Packet packet = PacketMaker.G_TO_U_MOVE(object_info);
            foreach (var user_subject in this.object_instacne_dict[current_instance_key].ToList())
            {
                this.nats_client!.Publish(user_subject, packet.ToBytes());
            }

            Packet.Destroy(packet);
        }

        public void LeaveManageObject(RedisValue message)
        {
            (string position_key, string object_key) = MessagePackSerializer.Deserialize<(
                string,
                string
            )>(message);

            var instacen_key = MapHelper.ConvertToInstanceKey(position_key);
            lock (position_lock)
            {
                this.object_instacne_dict[instacen_key].Remove(object_key);
            }
        }

        public void SpawnManageObject(RedisValue message)
        {
            (string user_subject, List<string> instance_key_list) =
                MessagePackSerializer.Deserialize<(string, List<string>)>(message);

            var spawn_list = new List<string>();
            foreach (var instance_key in instance_key_list)
            {
                spawn_list.AddRange(this.object_instacne_dict[instance_key]);
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
            (string instance_key, string object_key) = MessagePackSerializer.Deserialize<(
                string,
                string
            )>(message);

            lock (position_lock)
            {
                var result = this.object_instacne_dict[instance_key].Remove(object_key);
            }

            Packet packet = PacketMaker.G_TO_U_DESTROY(object_key);
            foreach (var user_subject in this.object_instacne_dict[instance_key].ToList())
            {
                this.nats_client!.Publish(user_subject, packet.ToBytes());
            }

            Packet.Destroy(packet);
        }

        public void UpdatePlayerInfo(RedisValue message)
        {
            (string instance_key, PlayerInfo player_info) = MessagePackSerializer.Deserialize<(
                string,
                PlayerInfo
            )>(message);

            Packet packet = PacketMaker.G_TO_U_PLAYER_INFO(player_info);
            foreach (var channel in this.object_instacne_dict[instance_key].ToList())
            {
                this.nats_client!.Publish(channel, packet.ToBytes());
            }

            Packet.Destroy(packet);
        }

        void SubscribeInstance(MapID map_id, long map_sub_id)
        {
            this.nats_client!.Subscribe(
                MapHelper.GetMoveManageSubject(map_id, map_sub_id, Program.server_id),
                (subject, msg) =>
                {
                    try
                    {
                        MoveManageObject(msg);
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                }
            );

            this.nats_client.Subscribe(
                MapHelper.GetLeaveManageSubject(map_id, map_sub_id, Program.server_id),
                (subject, msg) =>
                {
                    try
                    {
                        LeaveManageObject(msg);
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                }
            );

            this.nats_client.Subscribe(
                MapHelper.GetSpawnManageSubject(map_id, map_sub_id, Program.server_id),
                (subject, msg) =>
                {
                    try
                    {
                        SpawnManageObject(msg);
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                }
            );

            this.nats_client.Subscribe(
                MapHelper.GetDestroyObjectSubject(map_id, map_sub_id, Program.server_id),
                (subject, msg) =>
                {
                    try
                    {
                        DestroyManageObject(msg);
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                }
            );

            this.nats_client.Subscribe(
                MapHelper.GetUpdatePlayerSubject(map_id, map_sub_id, Program.server_id),
                (subject, msg) =>
                {
                    try
                    {
                        UpdatePlayerInfo(msg);
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                }
            );
        }
    }
}
