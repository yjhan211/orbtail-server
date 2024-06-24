namespace game_server
{
    using System.Collections.Concurrent;
    using MessagePack;
    using network;
    using StackExchange.Redis;
    using user_server;

    public class InstanceController
    {
        NatsClient? nats_client;
        readonly ConcurrentDictionary<string, ConcurrentBag<string>> object_instance_dict; // Instance_id, [object_key, ..]
        readonly object position_lock = new object();

        public InstanceController()
        {
            this.object_instance_dict = new ConcurrentDictionary<string, ConcurrentBag<string>>();
        }

        public void Initialize(NatsClient nats_client)
        {
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
            Packet? packet = null;
            try
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
                        this.object_instance_dict.GetOrAdd(
                            instance_key,
                            _ => new ConcurrentBag<string>()
                        );
                        SubscribeInstance(map_id, map_sub_id);
                        break;

                    default:
                        throw new Exception("Invalid map id");
                }

                packet = PacketMaker.G_TO_U_CREATE_INSTANCE_SUCCESS(map_id, map_sub_id);
                this.nats_client!.Publish(user_subject, packet.ToBytes());
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet != null)
                {
                    Packet.Destroy(packet);
                }
            }
        }

        public void MoveManageObject(RedisValue message)
        {
            Packet? packet = null;
            try
            {
                var (last_position_key, object_info) = MessagePackSerializer.Deserialize<(
                    string,
                    GameObjectInfo
                )>(message);

                var object_key = GameObjectInfo.MakeHashField(
                    ObjectType.PLAYER,
                    object_info.object_id
                );
                var last_instance_key = MapHelper.ConvertToInstanceKey(last_position_key);
                var current_instance_key = MapHelper.GetInstanceKey(
                    object_info.map_id,
                    object_info.map_sub_id
                );

                UpdateObjectPosition(last_instance_key, current_instance_key, object_key);

                packet = PacketMaker.G_TO_U_MOVE(object_info);
                BroadcastToInstance(current_instance_key, packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet != null)
                {
                    Packet.Destroy(packet);
                }
            }
        }

        private void UpdateObjectPosition(
            string last_instance_key,
            string current_instance_key,
            string object_key
        )
        {
            if (this.object_instance_dict.TryGetValue(last_instance_key, out var lastInstanceBag))
            {
                var updatedBag = new ConcurrentBag<string>(
                    lastInstanceBag.Where(x => x != object_key)
                );
                this.object_instance_dict[last_instance_key] = updatedBag;
            }

            this.object_instance_dict.AddOrUpdate(
                current_instance_key,
                new ConcurrentBag<string> { object_key },
                (_, bag) =>
                {
                    bag.Add(object_key);
                    return bag;
                }
            );
        }

        public void LeaveManageObject(RedisValue message)
        {
            try
            {
                (string position_key, string object_key) = MessagePackSerializer.Deserialize<(
                    string,
                    string
                )>(message);

                var instance_key = MapHelper.ConvertToInstanceKey(position_key);
                if (this.object_instance_dict.TryGetValue(instance_key, out var instanceBag))
                {
                    var updatedBag = new ConcurrentBag<string>(
                        instanceBag.Where(x => x != object_key)
                    );
                    this.object_instance_dict[instance_key] = updatedBag;
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
        }

        public void SpawnManageObject(RedisValue message)
        {
            Packet? packet = null;
            try
            {
                (string user_subject, List<string> instance_key_list) =
                    MessagePackSerializer.Deserialize<(string, List<string>)>(message);

                var spawn_list = new List<string>();
                foreach (var instance_key in instance_key_list)
                {
                    if (this.object_instance_dict.TryGetValue(instance_key, out var objects))
                    {
                        spawn_list.AddRange(objects);
                    }
                }

                if (spawn_list.Count > 0)
                {
                    packet = PacketMaker.G_TO_U_SPAWN(spawn_list);
                    this.nats_client!.Publish(user_subject, packet.ToBytes());
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet != null)
                {
                    Packet.Destroy(packet);
                }
            }
        }

        public void DestroyManageObject(RedisValue message)
        {
            Packet? packet = null;
            try
            {
                (string instance_key, string object_key) = MessagePackSerializer.Deserialize<(
                    string,
                    string
                )>(message);

                if (this.object_instance_dict.TryGetValue(instance_key, out var instanceBag))
                {
                    var updatedBag = new ConcurrentBag<string>(
                        instanceBag.Where(x => x != object_key)
                    );
                    this.object_instance_dict[instance_key] = updatedBag;
                }

                packet = PacketMaker.G_TO_U_DESTROY(object_key);
                BroadcastToInstance(instance_key, packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet != null)
                {
                    Packet.Destroy(packet);
                }
            }
        }

        public void UpdatePlayerInfo(RedisValue message)
        {
            Packet? packet = null;
            try
            {
                (string instance_key, PlayerInfo player_info) = MessagePackSerializer.Deserialize<(
                    string,
                    PlayerInfo
                )>(message);

                packet = PacketMaker.G_TO_U_PLAYER_INFO(player_info);
                BroadcastToInstance(instance_key, packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet != null)
                {
                    Packet.Destroy(packet);
                }
            }
        }

        private void BroadcastToInstance(string instance_key, Packet packet)
        {
            if (this.object_instance_dict.TryGetValue(instance_key, out var channels))
            {
                foreach (var channel in channels)
                {
                    try
                    {
                        this.nats_client!.Publish(channel, packet.ToBytes());
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                }
            }
        }

        void SubscribeInstance(MapID map_id, long map_sub_id)
        {
            var subscriptions = new Dictionary<string, Action<RedisValue>>
            {
                {
                    MapHelper.GetMoveManageSubject(map_id, map_sub_id, Program.server_id),
                    MoveManageObject
                },
                {
                    MapHelper.GetLeaveManageSubject(map_id, map_sub_id, Program.server_id),
                    LeaveManageObject
                },
                {
                    MapHelper.GetSpawnManageSubject(map_id, map_sub_id, Program.server_id),
                    SpawnManageObject
                },
                {
                    MapHelper.GetDestroyObjectSubject(map_id, map_sub_id, Program.server_id),
                    DestroyManageObject
                },
                {
                    MapHelper.GetUpdatePlayerSubject(map_id, map_sub_id, Program.server_id),
                    UpdatePlayerInfo
                }
            };

            foreach (var subscription in subscriptions)
            {
                this.nats_client!.Subscribe(
                    subscription.Key,
                    (_, msg) =>
                    {
                        try
                        {
                            subscription.Value(msg);
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
}
