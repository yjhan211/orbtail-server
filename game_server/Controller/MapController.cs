namespace game_server
{
    using System.Collections.Concurrent;
    using System.Diagnostics;
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

        ConcurrentDictionary<string, List<string>> object_position_dict;
        ConcurrentDictionary<int, List<JobResourceInfo>> job_resource_dict;
        List<Cell> manage_cell_list;

        Task? create_job_resource_task;
        public CancellationTokenSource cts;

        public MapController(MapID map_id)
        {
            this.map_id = map_id;
            this.object_position_dict = new();
            this.job_resource_dict = new();
            this.manage_cell_list = new();

            this.cts = new();
        }

        public async void Initialize(NatsClient nats_client)
        {
            this.redis_connection = RedisConnectionPool.GetConnection();
            this.cache_helper = new CacheHelper(redis_connection);
            this.nats_client = nats_client;

            // 임시
            if (Program.server_id == 1)
            {
                var job_resource_values = await this.cache_helper.HashGetAll("job_resource_info");
                if (job_resource_values != null)
                {
                    foreach (HashEntry entry in job_resource_values)
                    {
                        if (!long.TryParse(entry.Name, out long job_resource_id))
                        {
                            continue;
                        }

                        await JobResourceController.Delete(this.cache_helper, job_resource_id);
                        await GameObjectInfoController.Delete(
                            this.cache_helper,
                            GameObjectInfo.MakeHashField(ObjectType.JOBRESOURCE, job_resource_id)
                        );
                    }
                }
            }
            else
            {
                await Task.Delay(10000);
            }

            this.nats_client.Subscribe(
                MapHelper.GetMoveManageSubject(this.map_id, 0, Program.server_id),
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
                MapHelper.GetLeaveManageSubject(this.map_id, 0, Program.server_id),
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
                MapHelper.GetSpawnManageSubject(this.map_id, 0, Program.server_id),
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
                MapHelper.GetDestroyObjectSubject(this.map_id, 0, Program.server_id),
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
                MapHelper.GetUpdatePlayerSubject(this.map_id, 0, Program.server_id),
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

            this.nats_client.Subscribe(
                MapHelper.GetUpdateJobResourceSubject(this.map_id, 0, Program.server_id),
                (subject, msg) =>
                {
                    try
                    {
                        UpdateJobResourceInfo(msg);
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                }
            );

            this.nats_client.Subscribe(
                MapHelper.GetUpdateCampSubject(this.map_id, 0, Program.server_id),
                (subject, msg) =>
                {
                    try
                    {
                        UpdateCampInfo(msg);
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                }
            );

            this.nats_client.Subscribe(
                MapHelper.GetBrodcastMoveSubject(this.map_id, 0, Program.server_id),
                (subject, msg) =>
                {
                    try
                    {
                        BroadcastUpdateObject(msg);
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                }
            );

            this.nats_client.Subscribe(
                MapHelper.GetBrodcastDestroySubject(this.map_id, 0, Program.server_id),
                (subject, msg) =>
                {
                    try
                    {
                        BroadcastDestroyObject(msg);
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                }
            );

            var manage_part_list = MapHelper.GetManagePartList(
                Program.game_server_num,
                Program.server_id
            );

            var manage_position_key_list = new List<string>();

            foreach (var manage_part in manage_part_list)
            {
                manage_position_key_list.AddRange(
                    MapHelper.position_list_by_map_part[this.map_id][manage_part]
                );

                this.job_resource_dict[manage_part] = new();
            }

            foreach (var position_key in manage_position_key_list)
            {
                this.object_position_dict[position_key] = new();
                manage_cell_list.Add(MapHelper.GetCell(position_key));
            }

            if (this.map_id == MapID.FOREST_1)
            {
                this.create_job_resource_task = Task.Run(CreateJobResourceTask, this.cts.Token);
            }
        }

        object position_lock = new();

        async Task CreateJobResourceTask()
        {
            Random random = new();

            while (!this.cts.Token.IsCancellationRequested)
            {
                try
                {
                    foreach (var part_resource_info in this.job_resource_dict)
                    {
                        // TODO 3 Config로 분리..
                        if (3 <= part_resource_info.Value.Count)
                        {
                            continue;
                        }

                        // // TODO csv로 정리
                        // if (50 < random.Next(0, 100))
                        // {
                        //     continue;
                        // }

                        var create_cell = this.manage_cell_list[
                            random.Next(0, this.manage_cell_list.Count)
                        ];

                        var create_position_key = MapHelper.GetPositionKey(
                            this.map_id,
                            0,
                            create_cell
                        );

                        long resource_uid = await this.cache_helper!.StringIncrement(
                            "temp_job_resource_uid"
                        );

                        GameObjectInfo object_info =
                            new()
                            {
                                object_type = ObjectType.JOBRESOURCE,
                                object_id = resource_uid,
                                current_cell = create_cell,
                                target_cell = create_cell,
                                map_id = MapID.FOREST_1,
                            };

                        // TODO resource_id 정리, 확률 기반으로 종류 결정
                        List<int> gen_resource_type_list = new() { 10001, 20001 };
                        JobResourceInfo job_resource_info =
                            new(
                                resource_uid,
                                gen_resource_type_list[
                                    random.Next(0, gen_resource_type_list.Count)
                                ],
                                object_info
                            );

                        this.job_resource_dict[part_resource_info.Key].Add(job_resource_info);
                        this.object_position_dict[create_position_key].Add(
                            object_info.GetHashField()
                        );

                        await JobResourceController.Save(this.cache_helper, job_resource_info);

                        // bound_cell이 포함된 서버에는 브로드캐스트 명령을 보냄
                        var target_server_list = MapHelper.GetBoundServerList(
                            this.map_id,
                            Program.game_server_num,
                            object_info.current_cell
                        );

                        foreach (var target_server in target_server_list)
                        {
                            this.nats_client!.Publish(
                                MapHelper.GetBrodcastMoveSubject(this.map_id, 0, target_server),
                                MessagePackSerializer.Serialize((create_position_key, object_info))
                            );
                        }
                    }

                    await Task.Delay(1000);
                }
                catch (Exception e)
                {
                    LogManager.WriteErrorLog(e);
                    break;
                }
            }

            try
            {
                this.create_job_resource_task!.Wait();
            }
            catch (AggregateException e)
            {
                LogManager.WriteErrorLog(e);
            }
        }

        public void MoveManageObject(RedisValue message)
        {
            var (last_position_key, object_info) = MessagePackSerializer.Deserialize<(
                string,
                GameObjectInfo
            )>(message);

            var object_key = GameObjectInfo.MakeHashField(
                object_info.object_type,
                object_info.object_id
            );

            var current_position_key = MapHelper.GetPositionKey(
                object_info.map_id,
                0,
                object_info.current_cell
            );

            // 위치 갱신
            lock (position_lock)
            {
                // last를 관리하는 서버가 본인이면 지움
                if (this.object_position_dict.TryGetValue(last_position_key, out _))
                {
                    foreach (var kvp in this.object_position_dict.ToList())
                    {
                        kvp.Value.Remove(object_key);
                    }
                }

                // current 추가
                this.object_position_dict[current_position_key].Add(object_key);
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
                    MapHelper.GetBrodcastMoveSubject(this.map_id, 0, target_server),
                    MessagePackSerializer.Serialize((current_position_key, object_info))
                );
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
                foreach (var kvp in this.object_position_dict.ToList())
                {
                    kvp.Value.Remove(object_key);
                }
            }
        }

        public void SpawnManageObject(RedisValue message)
        {
            (string user_subject, List<string> position_key_list) =
                MessagePackSerializer.Deserialize<(string, List<string>)>(message);

            var spawn_list = new List<string>();
            foreach (var position_key in position_key_list)
            {
                spawn_list.AddRange(this.object_position_dict[position_key]);
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
                foreach (var kvp in this.object_position_dict.ToList())
                {
                    kvp.Value.Remove(object_key);
                }

                foreach (var kvp in this.job_resource_dict.ToList())
                {
                    var result = kvp.Value.RemoveAll(
                        job_resource => job_resource.object_info.GetHashField() == object_key
                    );
                }
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
                    MapHelper.GetBrodcastDestroySubject(this.map_id, 0, target_server),
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
                var bound_position_key = MapHelper.GetPositionKey(this.map_id, 0, bound_cell);
                if (
                    !this.object_position_dict.TryGetValue(bound_position_key, out var channel_list)
                )
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

        public void UpdateJobResourceInfo(RedisValue message)
        {
            (string position_key, JobResourceInfo job_resource_info) =
                MessagePackSerializer.Deserialize<(string, JobResourceInfo)>(message);

            Packet packet = PacketMaker.G_TO_U_JOB_RESOURCE_INFO(job_resource_info);

            var pivot_cell = MapHelper.GetCell(position_key);
            var bound_cell_list = MapHelper.GetBoundCellList(pivot_cell);

            foreach (var bound_cell in bound_cell_list)
            {
                var bound_position_key = MapHelper.GetPositionKey(this.map_id, 0, bound_cell);
                if (
                    !this.object_position_dict.TryGetValue(bound_position_key, out var channel_list)
                )
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

        public void UpdateCampInfo(RedisValue message)
        {
            (string position_key, CampInfo camp_info) = MessagePackSerializer.Deserialize<(
                string,
                CampInfo
            )>(message);

            Packet packet = PacketMaker.G_TO_U_CAMP_INFO(camp_info);

            var pivot_cell = MapHelper.GetCell(position_key);
            var bound_cell_list = MapHelper.GetBoundCellList(pivot_cell);

            foreach (var bound_cell in bound_cell_list)
            {
                var bound_position_key = MapHelper.GetPositionKey(this.map_id, 0, bound_cell);
                if (
                    !this.object_position_dict.TryGetValue(bound_position_key, out var channel_list)
                )
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
                var bound_position_key = MapHelper.GetPositionKey(this.map_id, 0, bound_cell);
                if (
                    !this.object_position_dict.TryGetValue(bound_position_key, out var channel_list)
                )
                {
                    continue;
                }

                if (channel_list.Count <= 0)
                {
                    continue;
                }

                foreach (var channel in channel_list.ToList())
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
                var bound_position_key = MapHelper.GetPositionKey(this.map_id, 0, bound_cell);
                if (
                    !this.object_position_dict.TryGetValue(bound_position_key, out var channel_list)
                )
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
