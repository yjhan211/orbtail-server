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
        NatsClient? nats_client;

        readonly ConcurrentDictionary<string, ConcurrentBag<string>> object_position_dict;
        readonly ConcurrentDictionary<int, ConcurrentBag<JobResourceInfo>> job_resource_dict;
        readonly List<Cell> manage_cell_list;
        readonly CancellationTokenSource cts;
        readonly SemaphoreSlim job_resource_semaphore;

        public MapController(MapID map_id)
        {
            this.map_id = map_id;
            this.object_position_dict = new();
            this.job_resource_dict = new();
            this.manage_cell_list = new();
            this.cts = new();
            this.job_resource_semaphore = new SemaphoreSlim(1, 1);
        }

        public async Task Initialize(NatsClient nats_client)
        {
            this.nats_client = nats_client;

            // 임시
            if (Program.server_id != 1)
            {
                var job_resource_values = await CacheHelper.Instance.HashGetAllAsync(
                    "job_resource_info"
                );

                if (job_resource_values != null)
                {
                    foreach (HashEntry entry in job_resource_values)
                    {
                        if (!long.TryParse(entry.Name, out long job_resource_id))
                        {
                            continue;
                        }

                        await JobResourceInfo.Delete(job_resource_id);

                        var objecte_field = GameObjectInfo.MakeHashField(
                            ObjectType.JOBRESOURCE,
                            job_resource_id
                        );
                        await GameObjectInfo.Delete(objecte_field);
                    }

                    job_resource_values = null;
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

            InitializeManageParts();

            if (this.map_id == MapID.FACTORY_1)
            {
                _ = Task.Run(CreateJobResourceTask, this.cts.Token);
            }
        }

        void InitializeManageParts()
        {
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
                this.job_resource_dict[manage_part] = new ConcurrentBag<JobResourceInfo>();
            }

            foreach (var position_key in manage_position_key_list)
            {
                this.object_position_dict[position_key] = new ConcurrentBag<string>();
                manage_cell_list.Add(MapHelper.GetCell(position_key));
            }
        }

        async Task CreateJobResourceTask()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(this.cts.Token))
            {
                try
                {
                    await CreateJobResource();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    LogManager.WriteErrorLog(e);
                }
            }
        }

        async Task CreateJobResource()
        {
            await this.job_resource_semaphore.WaitAsync();
            try
            {
                Random random = new();
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
                    var create_position_key = MapHelper.GetPositionKey(this.map_id, 0, create_cell);

                    long resource_uid = await CacheHelper.Instance.StringIncrementAsync(
                        "temp_job_resource_uid"
                    );

                    GameObjectInfo object_info =
                        new()
                        {
                            object_type = ObjectType.JOBRESOURCE,
                            object_id = resource_uid,
                            current_cell = create_cell,
                            target_cell = create_cell,
                            map_id = MapID.FACTORY_1,
                        };

                    // TODO resource_id 정리, 확률 기반으로 종류 결정
                    List<int> gen_resource_type_list = new() { 10001, 20001 };
                    JobResourceInfo job_resource_info =
                        new(
                            resource_uid,
                            gen_resource_type_list[random.Next(0, gen_resource_type_list.Count)],
                            object_info
                        );

                    part_resource_info.Value.Add(job_resource_info);
                    this.object_position_dict.AddOrUpdate(
                        create_position_key,
                        new ConcurrentBag<string> { object_info.GetHashField() },
                        (_, bag) =>
                        {
                            bag.Add(object_info.GetHashField());
                            return bag;
                        }
                    );

                    await job_resource_info.Save();

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
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                this.job_resource_semaphore.Release();
            }
        }

        private void BroadcastToChannels(string position_key, Packet packet)
        {
            try
            {
                var pivot_cell = MapHelper.GetCell(position_key);
                var bound_cell_list = MapHelper.GetBoundCellList(pivot_cell);

                foreach (var bound_cell in bound_cell_list)
                {
                    var bound_position_key = MapHelper.GetPositionKey(this.map_id, 0, bound_cell);
                    if (
                        this.object_position_dict.TryGetValue(
                            bound_position_key,
                            out var channel_list
                        ) && channel_list.Any()
                    )
                    {
                        foreach (var channel in channel_list)
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
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
        }

        public void UpdateJobResourceInfo(RedisValue message)
        {
            Packet? packet = null;
            try
            {
                (string position_key, JobResourceInfo job_resource_info) =
                    MessagePackSerializer.Deserialize<(string, JobResourceInfo)>(message);
                packet = PacketMaker.G_TO_U_JOB_RESOURCE_INFO(job_resource_info);
                BroadcastToChannels(position_key, packet);
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

        public void UpdateCampInfo(RedisValue message)
        {
            Packet? packet = null;
            try
            {
                (string position_key, CampInfo camp_info) = MessagePackSerializer.Deserialize<(
                    string,
                    CampInfo
                )>(message);
                packet = PacketMaker.G_TO_U_CAMP_INFO(camp_info);
                BroadcastToChannels(position_key, packet);
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

        public void BroadcastUpdateObject(RedisValue message)
        {
            Packet? packet = null;
            try
            {
                var (position_key, object_info) = MessagePackSerializer.Deserialize<(
                    string,
                    GameObjectInfo
                )>(message);
                packet = PacketMaker.G_TO_U_MOVE(object_info);
                BroadcastToChannels(position_key, packet);
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

        void BroadcastObjectMove(string current_position_key, GameObjectInfo object_info)
        {
            var target_server_list = MapHelper.GetBoundServerList(
                this.map_id,
                Program.game_server_num,
                object_info.current_cell
            );

            var message = MessagePackSerializer.Serialize((current_position_key, object_info));
            foreach (var target_server in target_server_list)
            {
                this.nats_client!.Publish(
                    MapHelper.GetBrodcastMoveSubject(this.map_id, 0, target_server),
                    message
                );
            }
        }

        void UpdateObjectPosition(
            string last_position_key,
            string current_position_key,
            string object_key
        )
        {
            if (this.object_position_dict.TryGetValue(last_position_key, out var lastPositionBag))
            {
                var updatedBag = new ConcurrentBag<string>(
                    lastPositionBag.Where(x => x != object_key)
                );
                this.object_position_dict[last_position_key] = updatedBag;
            }

            this.object_position_dict.AddOrUpdate(
                current_position_key,
                new ConcurrentBag<string> { object_key },
                (_, bag) =>
                {
                    bag.Add(object_key);
                    return bag;
                }
            );
        }

        public void MoveManageObject(RedisValue message)
        {
            try
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

                UpdateObjectPosition(last_position_key, current_position_key, object_key);
                BroadcastObjectMove(current_position_key, object_info);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
        }

        public void LeaveManageObject(RedisValue message)
        {
            try
            {
                (string position_key, string object_key) = MessagePackSerializer.Deserialize<(
                    string,
                    string
                )>(message);

                foreach (var kvp in this.object_position_dict)
                {
                    var updatedBag = new ConcurrentBag<string>(
                        kvp.Value.Where(x => x != object_key)
                    );
                    this.object_position_dict[kvp.Key] = updatedBag;
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
                (string user_subject, List<string> position_key_list) =
                    MessagePackSerializer.Deserialize<(string, List<string>)>(message);

                var spawn_list = new List<string>();
                foreach (var position_key in position_key_list)
                {
                    if (this.object_position_dict.TryGetValue(position_key, out var objects))
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
            try
            {
                var (position_key, object_key) = MessagePackSerializer.Deserialize<(
                    string,
                    string
                )>(message);

                // Update object_position_dict
                foreach (var kvp in this.object_position_dict)
                {
                    var updatedBag = new ConcurrentBag<string>(
                        kvp.Value.Where(x => x != object_key)
                    );
                    this.object_position_dict[kvp.Key] = updatedBag;
                }

                // Update job_resource_dict
                foreach (var kvp in this.job_resource_dict)
                {
                    var updatedBag = new ConcurrentBag<JobResourceInfo>(
                        kvp.Value.Where(
                            job_resource => job_resource.object_info.GetHashField() != object_key
                        )
                    );
                    this.job_resource_dict[kvp.Key] = updatedBag;
                }

                // Broadcast destroy message
                Cell position_cell = MapHelper.GetCell(position_key);
                var target_server_list = MapHelper.GetBoundServerList(
                    this.map_id,
                    Program.game_server_num,
                    position_cell
                );

                var destroyMessage = MessagePackSerializer.Serialize((position_key, object_key));
                foreach (var target_server in target_server_list)
                {
                    this.nats_client!.Publish(
                        MapHelper.GetBrodcastDestroySubject(this.map_id, 0, target_server),
                        destroyMessage
                    );
                }

                Packet? packet = null;
                try
                {
                    packet = PacketMaker.G_TO_U_DESTROY(object_key);
                    BroadcastToChannels(position_key, packet);
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
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
        }

        public void UpdatePlayerInfo(RedisValue message)
        {
            Packet? packet = null;
            try
            {
                (string position_key, PlayerInfo player_info) = MessagePackSerializer.Deserialize<(
                    string,
                    PlayerInfo
                )>(message);
                packet = PacketMaker.G_TO_U_PLAYER_INFO(player_info);
                BroadcastToChannels(position_key, packet);
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

        public void BroadcastDestroyObject(RedisValue message)
        {
            Packet? packet = null;
            try
            {
                var (position_key, object_key) = MessagePackSerializer.Deserialize<(
                    string,
                    string
                )>(message);
                packet = PacketMaker.G_TO_U_DESTROY(object_key);
                BroadcastToChannels(position_key, packet);
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
    }
}
