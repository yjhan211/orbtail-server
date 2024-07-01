namespace user_server
{
    using network;
    using MessagePack;
    using StackExchange.Redis;
    using RedLockNet.SERedis;
    using System.Diagnostics;

    public class GameUser : IPeer
    {
        public UserToken token { get; private set; }
        public RedLockFactory redlock { get; private set; }
        public SemaphoreSlim player_lock { get; private set; }
        public CancellationTokenSource cts { get; private set; }
        public NatsClient nats_client { get; private set; }

        /*-------------------------------------------------------------*/

        public long player_id { get; private set; }
        public GameObjectController? object_controller { get; set; }

        /*-------------------------------------------------------------*/
        public bool in_action { get; set; }
        public ExploreTargetInfo? current_progress_explore { get; set; }
        public (int, JobResourceInfo)? current_progress_job { get; set; }
        public (DateTime, (MapID, long, Cell, bool))? change_map_task { get; set; }
        public CampInfo? current_camp_info { get; set; }

        public DateTime debuff_task { get; set; }

        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.is_alive = true;
            this.token.is_released = false;
            this.token.SetPeer(this);

            this.redlock = RedisConnectionPool.GetRedLockFactory();
            this.nats_client = new(Program.nats_endpoint);

            this.player_id = 0;
            this.player_lock = new(1);

            this.cts = new();
        }

        void HandleMessage<T>(byte[] body, Func<GameUser, T, Task> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            handleMessage(this, msg);
        }

        void HandleMessage<T>(byte[] body, Action<GameUser, T> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            handleMessage(this, msg);
        }

        public async Task OnMessageFromClient(Const<byte[]> buffer)
        {
            try
            {
                if (Config.BUFFER_SIZE < buffer.Value.Length)
                {
                    throw new Exception(
                        $"Invalid Buffer Size. player id: {this.player_id}, size: {buffer.Value.Length}"
                    );
                }

                byte[] clone = new byte[Config.BUFFER_SIZE];
                Array.Copy(buffer.Value, clone, buffer.Value.Length);

                Packet packet = new(clone, this);
                PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
                long player_id = packet.PopPlayerId();
                byte[] body = packet.PopBody();

                // LogManager.WriteDebugLog($"PROTOCOL: {protocol_id}");

                var non_auth_protocol = new[] { PROTOCOL.C_TO_U_HEART_BEAT, PROTOCOL.C_TO_U_LOGIN };
                if (non_auth_protocol.Contains(protocol_id))
                {
                    switch (protocol_id)
                    {
                        case PROTOCOL.C_TO_U_HEART_BEAT:
                            await HeartBeat();
                            break;
                        case PROTOCOL.C_TO_U_LOGIN:
                            HandleMessage<C_TO_U_LOGIN>(body, Login);
                            break;
                    }
                }
                else
                {
                    if (player_id == 0 || this.player_id != player_id)
                    {
                        throw new Exception($"Invalid ID: {this.player_id}, {player_id}");
                    }

                    if (this.object_controller == null)
                    {
                        throw new Exception($"not initialize state {this.player_id}, {player_id}");
                    }

                    var action_protocol = new[]
                    {
                        PROTOCOL.C_TO_U_MOVE,
                        PROTOCOL.C_TO_U_WEAR_ITEM,
                        PROTOCOL.C_TO_U_USE_SKILL
                    };

                    if (action_protocol.Contains(protocol_id) && this.in_action)
                    {
                        throw new Exception($"in action. {this.player_id}");
                    }

                    switch (protocol_id)
                    {
                        case PROTOCOL.C_TO_U_CHANGE_MAP_SUCCESS:
                            await ChangeMapSuccess();
                            break;
                        case PROTOCOL.C_TO_U_CHAT_LOG:
                            await ChatController.GetChatHistory(this, ChatType.ALL);
                            break;
                        case PROTOCOL.C_TO_U_MOVE:
                            HandleMessage<C_TO_U_MOVE>(body, this.object_controller.RequestMove);
                            break;
                        case PROTOCOL.C_TO_U_PLAYER_INFO:
                            HandleMessage<C_TO_U_PLAYER_INFO>(body, GetPlayerInfo);
                            break;
                        case PROTOCOL.C_TO_U_OBJECT_INFO:
                            HandleMessage<C_TO_U_OBJECT_INFO>(
                                body,
                                this.object_controller.GetObjectInfo
                            );
                            break;
                        case PROTOCOL.C_TO_U_EXPLORE_TARGET_INFO:
                            HandleMessage<C_TO_U_EXPLORE_TARGET_INFO>(body, GetExploreTargetInfo);
                            break;
                        case PROTOCOL.C_TO_U_JOB_RESOURCE_INFO:
                            HandleMessage<C_TO_U_JOB_RESOURCE_INFO>(body, GetJobResourceInfo);
                            break;
                        case PROTOCOL.C_TO_U_UPGRADE_JOB:
                            HandleMessage<C_TO_U_UPGRADE_JOB>(body, JobController.UpgradeJob);
                            break;
                        case PROTOCOL.C_TO_U_WEAR_ITEM:
                            HandleMessage<C_TO_U_WEAR_ITEM>(
                                body,
                                InventoryController.RequestWearItem
                            );
                            break;
                        case PROTOCOL.C_TO_U_USE_ITEM:
                            HandleMessage<C_TO_U_USE_ITEM>(
                                body,
                                InventoryController.RequestUseItem
                            );
                            break;
                        case PROTOCOL.C_TO_U_EXPLORE:
                            HandleMessage<C_TO_U_EXPLORE>(body, JobController.Explore);
                            break;
                        case PROTOCOL.C_TO_U_USE_SKILL:
                            HandleMessage<C_TO_U_USE_SKILL>(body, JobController.UseJobSkill);
                            break;
                        case PROTOCOL.C_TO_U_CHAT_MSG:
                            HandleMessage<C_TO_U_CHAT_MSG>(body, ChatController.SendChat);
                            break;
                        case PROTOCOL.C_TO_U_CREATE_LAB:
                            HandleMessage<C_TO_U_CREATE_LAB>(body, LabController.CreateLab);
                            break;
                        case PROTOCOL.C_TO_U_UPGRADE_RESEARCH:
                            HandleMessage<C_TO_U_UPGRADE_RESEARCH>(
                                body,
                                LabController.UpgradeResearch
                            );
                            break;
                        case PROTOCOL.C_TO_U_MAKE:
                            HandleMessage<C_TO_U_MAKE>(body, LabController.Make);
                            break;
                        case PROTOCOL.C_TO_U_WRITE_LAB_HIRE:
                            HandleMessage<C_TO_U_WRITE_LAB_HIRE>(body, LabController.WriteLabHire);
                            break;
                        case PROTOCOL.C_TO_U_LAB_HIRE_LIST:
                            await LabController.LabHireList(this);
                            break;
                        case PROTOCOL.C_TO_U_JOIN_LAB:
                            HandleMessage<C_TO_U_JOIN_LAB>(body, LabController.JoinLab);
                            break;
                        case PROTOCOL.C_TO_U_LAB_INVENTORY:
                            await InventoryController.GetLabInventory(this);
                            break;
                        case PROTOCOL.C_TO_U_LAB_INVENTORY_ADD_ITEM:
                            HandleMessage<C_TO_U_LAB_INVENTORY_ADD_ITEM>(
                                body,
                                InventoryController.AddLabItem
                            );
                            break;
                        case PROTOCOL.C_TO_U_LAB_INVENTORY_TAKE_ITEM:
                            HandleMessage<C_TO_U_LAB_INVENTORY_TAKE_ITEM>(
                                body,
                                InventoryController.TakeLabItem
                            );
                            break;
                        case PROTOCOL.C_TO_U_ENCAMP:
                            HandleMessage<C_TO_U_ENCAMP>(body, JobController.Encamp);
                            break;
                        case PROTOCOL.C_TO_U_DECAMP:
                            await JobController.Decamp(this);
                            break;
                        case PROTOCOL.C_TO_U_ADD_SELL_ITEM:
                            HandleMessage<C_TO_U_ADD_SELL_ITEM>(body, JobController.AddSellItem);
                            break;
                        case PROTOCOL.C_TO_U_DELETE_SELL_ITEM:
                            HandleMessage<C_TO_U_DELETE_SELL_ITEM>(
                                body,
                                JobController.DeleteSellItem
                            );
                            break;
                        case PROTOCOL.C_TO_U_BUY_ITEM:
                            HandleMessage<C_TO_U_BUY_ITEM>(body, JobController.BuyItem);
                            break;
                        case PROTOCOL.C_TO_U_CAMP_INFO:
                            HandleMessage<C_TO_U_CAMP_INFO>(body, GetCampInfo);
                            break;
                    }
                }

                Packet.Destroy(packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                this.player_lock.Release();
            }
        }

        public void OnMessageFromSubscribe(RedisValue message)
        {
            try
            {
                if (this.object_controller == null)
                {
                    return;
                }

                Packet packet = new((byte[])message!);
                PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
                long player_id = packet.PopPlayerId();
                var body = packet.PopBody();

                switch (protocol_id)
                {
                    case PROTOCOL.G_TO_U_MOVE:
                        HandleMessage<G_TO_U_MOVE>(body, this.object_controller.SubscribeMove);
                        break;

                    case PROTOCOL.G_TO_U_SPAWN:
                        HandleMessage<G_TO_U_SPAWN>(body, this.object_controller.SubscribeSpawn);
                        break;

                    case PROTOCOL.G_TO_U_DESTROY:
                        HandleMessage<G_TO_U_DESTROY>(
                            body,
                            this.object_controller.SubscribeDestroy
                        );
                        break;

                    case PROTOCOL.U_TO_C_CHAT_MSG:
                        HandleMessage<U_TO_C_CHAT_MSG>(body, SubscribeChatMsg);
                        break;

                    case PROTOCOL.G_TO_U_PLAYER_INFO:
                        HandleMessage<G_TO_U_PLAYER_INFO>(body, SubscribePlayerInfo);
                        break;

                    case PROTOCOL.G_TO_U_EXPLORE_TARGET_INFO:
                        HandleMessage<G_TO_U_EXPLORE_TARGET_INFO>(body, SubscribeExploreTargetInfo);
                        break;

                    case PROTOCOL.G_TO_U_JOB_RESOURCE_INFO:
                        HandleMessage<G_TO_U_JOB_RESOURCE_INFO>(body, SubscribeJobResourceInfo);
                        break;

                    case PROTOCOL.G_TO_U_CREATE_INSTANCE_SUCCESS:
                        HandleMessage<G_TO_U_CREATE_INSTANCE_SUCCESS>(
                            body,
                            this.object_controller.SubscribeCreateinstanceSuccess
                        );
                        break;

                    case PROTOCOL.U_TO_C_LAB_INFO:
                        HandleMessage<U_TO_C_LAB_INFO>(body, SubscribeLabInfo);
                        break;

                    case PROTOCOL.U_TO_U_LAB_INVENTORY:
                        HandleMessage<U_TO_U_LAB_INVENTORY>(body, SubscribeLabInventory);
                        break;

                    case PROTOCOL.G_TO_U_CAMP_INFO:
                        HandleMessage<G_TO_U_CAMP_INFO>(body, SubscribeCampInfo);
                        break;

                    case PROTOCOL.U_TO_U_PLAYER_INFO:
                        HandleMessage<U_TO_U_PLAYER_INFO>(body, SubscribePlayerInfo);
                        break;

                    case PROTOCOL.U_TO_U_DUPLICATE:
                        this.RecvDuplicate();
                        break;
                }

                Packet.Destroy(packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                // this.OnRemoved();
            }
        }

        async Task HeartBeat()
        {
            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_HEART_BEAT(DateTime.UtcNow);
                this.SendToClient(packet);
                this.token.is_alive = true;
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

            if (this.current_progress_explore != null)
            {
                await JobController.ExploreEnd(this, this.current_progress_explore);
            }

            if (this.current_progress_job != null)
            {
                await JobController.JobSkillEnd(
                    this,
                    this.current_progress_job.Value.Item1,
                    this.current_progress_job.Value.Item2
                );
            }

            if (this.change_map_task != null)
            {
                var change_time = this.change_map_task.Value.Item1;
                if (DateTime.UtcNow >= change_time)
                {
                    var change_info = this.change_map_task.Value.Item2;
                    await this.object_controller!.ChangeMap(
                        change_info.Item1,
                        change_info.Item2,
                        change_info.Item3,
                        change_info.Item4
                    );

                    this.change_map_task = null;
                }
            }

            if (this.object_controller != null)
            {
                if (this.object_controller.object_info.map_id == MapID.WETLAND_1)
                {
                    if (DateTime.UtcNow >= debuff_task)
                    {
                        PlayerInfo? player_info;
                        using (await PlayerInfo.Lock(this.redlock, this.player_id))
                        {
                            player_info = await PlayerInfo.Load(this.player_id);
                            if (player_info == null)
                            {
                                throw new Exception("cannot found player info");
                            }

                            bool has_item = player_info.wear_items.Contains(103000004);
                            if (!has_item)
                            {
                                player_info.job_info.hp -= 1;
                                await player_info.Save();

                                Packet? update_hp_packet = null;
                                try
                                {
                                    update_hp_packet = PacketMaker.U_TO_C_UPDATE_HP(
                                        -1,
                                        player_info.job_info.hp
                                    );
                                    this.SendToClient(update_hp_packet);
                                    this.token.is_alive = true;
                                }
                                catch (Exception e)
                                {
                                    LogManager.WriteErrorLog(e);
                                }
                                finally
                                {
                                    if (update_hp_packet != null)
                                    {
                                        Packet.Destroy(update_hp_packet);
                                    }
                                }
                            }
                        }

                        debuff_task = DateTime.UtcNow.AddSeconds(5);
                    }
                }
            }

            // if (current_camp_info != null)
            // {
            //     if (DateTime.UtcNow >= current_camp_info.add_hp_timestamp)
            //     {
            //         JobInfo? job_info;
            //         using (await PlayerInfo.Lock(this.redlock, this.player_id))
            //         {
            //             job_info = await JobInfo.Load(this.player_id);
            //             if (job_info == null)
            //             {
            //                 throw new Exception("cannot found job info");
            //             }

            //             if (100 > job_info.hp) // TODO 임시 하드코딩
            //             {
            //                 job_info.hp += 1;
            //                 current_camp_info.add_hp_timestamp = DateTime.UtcNow.AddSeconds(60);

            //                 await job_info.Save();
            //                 await current_camp_info.Save();

            //                 Packet? update_hp_packet = null;
            //                 try
            //                 {
            //                     update_hp_packet = PacketMaker.U_TO_C_UPDATE_HP(1, job_info.hp);
            //                     this.SendToClient(update_hp_packet);
            //                     this.token.is_alive = true;
            //                 }
            //                 catch (Exception e)
            //                 {
            //                     LogManager.WriteErrorLog(e);
            //                 }
            //                 finally
            //                 {
            //                     if (update_hp_packet != null)
            //                     {
            //                         Packet.Destroy(update_hp_packet);
            //                     }
            //                 }
            //             }
            //         }
            //     }
            // }
        }

        async Task Login(GameUser _, C_TO_U_LOGIN request)
        {
            if (this.player_id != 0)
            {
                throw new Exception("Already Has Player id");
            }

            long temp_player_id =
                request.account_token == "dummy"
                    ? await CacheHelper.Instance.StringIncrementAsync("temp_player_id") + 1000
                    : long.Parse(request.account_token);

            bool is_new = false;
            PlayerInfo? player_info = null;
            using (await PlayerInfo.Lock(this.redlock, this.player_id))
            {
                player_info = await PlayerInfo.Load(temp_player_id);
                if (player_info == null)
                {
                    // 플레이어 생성
                    player_info = new(
                        temp_player_id,
                        name: request.account_token == "dummy"
                            ? $"더미{temp_player_id}"
                            : $"플레이어{temp_player_id}",
                        request.account_token == "dummy" ? MapHelper.GetRandomCell() : new(88, 132)
                    );

                    is_new = true;
                    player_info.object_info.map_id = MapID.CAMPUS_1;
                    player_info.job_info.hp = 100;
                    player_info.gold = 10000;
                }

                player_info.object_info.current_cell = player_info.object_info.target_cell;
                player_info.state = PlayerState.NONE;

                if (is_new)
                {
                    // 기본 아이템 증정
                    List<ItemInfo> gift_item_list = new();

                    // 수습 연구원의 머리
                    var default_hair = await InventoryController.CreateItem(this, 101000001, 1);
                    // 수습 연구원의 제복
                    var default_top = await InventoryController.CreateItem(this, 103000001, 1);
                    // 수습 공학자의 헬멧
                    var default_hat_1 = await InventoryController.CreateItem(this, 102000001, 1);
                    // 수습 화학자의 고글
                    var default_hat_2 = await InventoryController.CreateItem(this, 102000002, 1);

                    // TODO 테스트 장화
                    var test_item = await InventoryController.CreateItem(this, 104000004, 1);
                    // TODO 테스트 우비
                    var test_item_2 = await InventoryController.CreateItem(this, 103000004, 1);
                    // TODO 테스트 좌판
                    var test_item_3 = await InventoryController.CreateItem(this, 401000002, 1);
                    // TODO 테스트 음식
                    var test_item_4 = await InventoryController.CreateItem(this, 202000007, 1);
                    var test_item_5 = await InventoryController.CreateItem(this, 202000008, 1);
                    var test_item_6 = await InventoryController.CreateItem(this, 202000009, 1);
                    var test_item_7 = await InventoryController.CreateItem(this, 202000010, 1);
                    var test_item_8 = await InventoryController.CreateItem(this, 202000011, 1);
                    var test_item_9 = await InventoryController.CreateItem(this, 202000012, 1);
                    var test_item_10 = await InventoryController.CreateItem(this, 202000013, 1);
                    var test_item_11 = await InventoryController.CreateItem(this, 202000014, 1);
                    var test_item_12 = await InventoryController.CreateItem(this, 202000015, 1);
                    var test_item_13 = await InventoryController.CreateItem(this, 202000016, 1);
                    var test_item_14 = await InventoryController.CreateItem(this, 202000017, 1);
                    var test_item_15 = await InventoryController.CreateItem(this, 202000018, 1);

                    gift_item_list.AddRange(
                        new[]
                        {
                            default_hair,
                            default_top,
                            default_hat_1,
                            default_hat_2,
                            test_item,
                            test_item_2,
                            test_item_3,
                            test_item_4,
                            test_item_5,
                            test_item_6,
                            test_item_7,
                            test_item_8,
                            test_item_9,
                            test_item_10,
                            test_item_11,
                            test_item_12,
                            test_item_13,
                            test_item_14,
                            test_item_15
                        }
                    );

                    player_info.inventory_info.AddItem(gift_item_list);
                    player_info.WearItem(default_hair.item_uid);
                    player_info.WearItem(default_top.item_uid);
                }
                else
                {
                    Packet? duplicate_packet = null;
                    try
                    {
                        duplicate_packet = Packet.Create((int)PROTOCOL.U_TO_U_DUPLICATE);
                        this.nats_client.Publish(
                            player_info.object_info.GetHashField(),
                            duplicate_packet.ToBytes()
                        );
                    }
                    catch (Exception e)
                    {
                        LogManager.WriteErrorLog(e);
                    }
                    finally
                    {
                        if (duplicate_packet != null)
                        {
                            Packet.Destroy(duplicate_packet);
                        }
                    }
                }

                // TODO 임시
                player_info.job_info.hp = 100;

                await player_info.Save();
                await player_info.object_info.Save();

                this.player_id = player_info.player_id;
                this.object_controller = new(this, player_info.object_info);
                this.in_action = false;
            }

            // 개인 구독 시작
            this.nats_client.Subscribe(
                player_info.object_info.GetHashField(),
                (channel, message) => OnMessageFromSubscribe(message)
            );

            // 공용 구독 시작
            this.nats_client.Subscribe(
                "all",
                (channel, message) => OnMessageFromSubscribe(message)
            );

            var lab_info = await LabInfo.Load(player_info.lab_id);

            Packet? login_packet = null;
            try
            {
                login_packet = PacketMaker.U_TO_C_LOGIN(player_info, lab_info ?? new());
                this.SendToClient(login_packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (login_packet != null)
                {
                    Packet.Destroy(login_packet);
                }
            }

            // 인벤토리 정보 전송
            await InventoryController.GetCurrentItemList(this);

            // 연구소 가입된경우 랩 인벤토리 정보 전송
            if (player_info.lab_id != 0)
            {
                await InventoryController.GetLabInventory(this);
            }

            await this.object_controller.ChangeMap(
                player_info.object_info.map_id,
                player_info.object_info.map_sub_id,
                player_info.object_info.current_cell,
                player_info.object_info.is_flip
            );
        }

        async Task ChangeMapSuccess()
        {
            var player_info = await PlayerInfo.Load(this.player_id);
            if (player_info == null)
            {
                throw new Exception("not found player info");
            }

            await this.object_controller!.Move(
                this.object_controller.object_info.current_cell,
                DirectionType.NONE,
                player_info,
                true
            );
        }

        async Task GetPlayerInfo(GameUser _, C_TO_U_PLAYER_INFO body)
        {
            var player_id_list = body.player_id_list;

            RedisValue[] keys = body.player_id_list.ConvertAll(x => (RedisValue)x).ToArray();
            var player_info_list = await PlayerInfo.LoadAll(keys);

            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_PLAYER_INFO(player_info_list);
                this.SendToClient(packet);
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

        async Task GetExploreTargetInfo(GameUser _, C_TO_U_EXPLORE_TARGET_INFO body)
        {
            var explore_target_id_list = body.explore_target_id_list;
            var explore_target_info_list = new List<ExploreTargetInfo>();

            for (int i = 0; i < explore_target_id_list.Count; i++)
            {
                var target_explore_uid = explore_target_id_list[i];
                ExploreTargetInfo? target_explore_info;

                using (await ExploreTargetInfo.Lock(this.redlock, target_explore_uid))
                {
                    target_explore_info = await ExploreTargetInfo.Load(target_explore_uid);
                }

                if (target_explore_info == null)
                {
                    LogManager.WriteDebugLog("target explore info null");
                    continue;
                }

                explore_target_info_list.Add(target_explore_info);

                bool is_max = explore_target_info_list.Count >= Config.BROADCAST_UNIT;
                bool is_ended =
                    i == explore_target_id_list.Count - 1
                    || explore_target_info_list.Count == explore_target_id_list.Count;

                if (is_max || is_ended)
                {
                    Packet? packet = null;
                    try
                    {
                        packet = PacketMaker.U_TO_C_EXPLORE_TARGET_INFO(explore_target_info_list);
                        this.SendToClient(packet);
                        explore_target_info_list.Clear();
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

        async Task GetJobResourceInfo(GameUser _, C_TO_U_JOB_RESOURCE_INFO body)
        {
            var job_resource_id_list = body.job_resource_id_list;
            var job_resource_info_list = new List<JobResourceInfo>();

            for (int i = 0; i < job_resource_id_list.Count; i++)
            {
                var target_resource_id = job_resource_id_list[i];
                JobResourceInfo? target_resource_info;

                using (await JobResourceInfo.Lock(this.redlock, target_resource_id))
                {
                    target_resource_info = await JobResourceInfo.Load(target_resource_id);
                }

                if (target_resource_info == null)
                {
                    continue;
                }

                job_resource_info_list.Add(target_resource_info);

                bool is_max = job_resource_info_list.Count >= Config.BROADCAST_UNIT;
                bool is_ended =
                    i == job_resource_id_list.Count - 1
                    || job_resource_info_list.Count == job_resource_id_list.Count;

                if (is_max || is_ended)
                {
                    Packet? packet = null;
                    try
                    {
                        packet = PacketMaker.U_TO_C_JOB_RESOURCE_INFO(job_resource_info_list);
                        this.SendToClient(packet);

                        job_resource_info_list.Clear();
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

        async Task GetCampInfo(GameUser _, C_TO_U_CAMP_INFO body)
        {
            var camp_id_list = body.camp_id_list;
            var camp_info_list = new List<CampInfo>();

            for (int i = 0; i < camp_id_list.Count; i++)
            {
                var target_camp_id = camp_id_list[i];
                CampInfo? target_camp_info = await CampInfo.Load(target_camp_id);

                if (target_camp_info == null)
                {
                    continue;
                }

                camp_info_list.Add(target_camp_info);

                bool is_max = camp_info_list.Count >= Config.BROADCAST_UNIT;
                bool is_ended = i == (camp_info_list.Count - 1);

                if (is_max || is_ended)
                {
                    Packet? packet = null;
                    try
                    {
                        packet = PacketMaker.U_TO_C_CAMP_INFO(camp_info_list);
                        this.SendToClient(packet);

                        camp_info_list.Clear();
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

        void SubscribePlayerInfo(GameUser _, G_TO_U_PLAYER_INFO body)
        {
            var player_info_list = new List<PlayerInfo> { body.player_info };

            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_PLAYER_INFO(player_info_list);
                this.SendToClient(packet);
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

        void SubscribeExploreTargetInfo(GameUser _, G_TO_U_EXPLORE_TARGET_INFO body)
        {
            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_EXPLORE_TARGET_INFO(new() { body.explore_target_info });
                this.SendToClient(packet);
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

        void SubscribeJobResourceInfo(GameUser _, G_TO_U_JOB_RESOURCE_INFO body)
        {
            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_JOB_RESOURCE_INFO(new() { body.job_resource_info });
                this.SendToClient(packet);
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

        void SubscribeChatMsg(GameUser _, U_TO_C_CHAT_MSG body)
        {
            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_CHAT_MSG(body.chat_type, body.name, body.chat_message);
                this.SendToClient(packet);
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

        void SubscribeLabInfo(GameUser _, U_TO_C_LAB_INFO body)
        {
            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_LAB_INFO(body.join_player_info, body.lab_info);
                this.SendToClient(packet);
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

        void SubscribeLabInventory(GameUser _, U_TO_U_LAB_INVENTORY body)
        {
            SendLabItemList(body.item_list);
        }

        void SubscribePlayerInfo(GameUser user, U_TO_U_PLAYER_INFO body)
        {
            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_PLAYER_INFO(new() { body.player_info });
                this.SendToClient(packet);

                _ = InventoryController.GetCurrentItemList(user);
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

        void SubscribeCampInfo(GameUser _, G_TO_U_CAMP_INFO body)
        {
            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_CAMP_INFO(new() { body.camp_info });
                this.SendToClient(packet);
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

        public void SendLabItemList(Dictionary<long, ItemInfo> item_dict)
        {
            if (item_dict.Count == 0)
            {
                Packet? packet = null;
                try
                {
                    packet = PacketMaker.U_TO_C_LAB_INVENTORY(new(), true);
                    this.SendToClient(packet);
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

            int index = 0;
            var item_keys = item_dict.Keys.ToArray();

            while (index < item_keys.Length)
            {
                var batch_dict = new Dictionary<long, ItemInfo>();

                for (int i = index; i < index + Config.BROADCAST_UNIT && i < item_keys.Length; i++)
                {
                    var key = item_keys[i];
                    batch_dict[key] = item_dict[key];
                }

                var is_ended = index + Config.BROADCAST_UNIT >= item_keys.Length;

                Packet? inventory_packet = null;
                try
                {
                    inventory_packet = PacketMaker.U_TO_C_LAB_INVENTORY(batch_dict, is_ended);
                    this.SendToClient(inventory_packet);

                    index += Config.BROADCAST_UNIT;
                }
                catch (Exception e)
                {
                    LogManager.WriteErrorLog(e);
                }
                finally
                {
                    if (inventory_packet != null)
                    {
                        Packet.Destroy(inventory_packet);
                    }
                }
            }
        }

        public void BroadcastUpdatePlayerInfo(PlayerInfo player_info)
        {
            switch (player_info.object_info.map_id)
            {
                case MapID.LAB_1:
                    var instance_key = MapHelper.GetInstanceKey(
                        player_info.object_info.map_id,
                        player_info.object_info.map_sub_id
                    );
                    var instance_server = MapHelper.GetServerIdByMapSubID(
                        Program.game_server_num,
                        player_info.object_info.map_sub_id
                    );
                    this.nats_client!.Publish(
                        MapHelper.GetUpdatePlayerSubject(
                            player_info.object_info.map_id,
                            player_info.object_info.map_sub_id,
                            instance_server
                        ),
                        MessagePackSerializer.Serialize((instance_key, player_info))
                    );
                    break;

                default:
                    var position_key = MapHelper.GetPositionKey(
                        player_info.object_info.map_id,
                        player_info.object_info.map_sub_id,
                        player_info.object_info.current_cell
                    );

                    var target_server_list = MapHelper.GetBoundServerList(
                        player_info.object_info.map_id,
                        Program.game_server_num,
                        MapHelper.GetCell(position_key)
                    );

                    foreach (var target_server in target_server_list)
                    {
                        this.nats_client!.Publish(
                            MapHelper.GetUpdatePlayerSubject(
                                player_info.object_info.map_id,
                                player_info.object_info.map_sub_id,
                                target_server
                            ),
                            MessagePackSerializer.Serialize((position_key, player_info))
                        );
                    }
                    break;
            }
        }

        public void BroadcastUpdateExploreTargetInfo(ExploreTargetInfo explore_target_info)
        {
            var position_key = MapHelper.GetPositionKey(
                explore_target_info.object_info.map_id,
                explore_target_info.object_info.map_sub_id,
                explore_target_info.object_info.current_cell
            );

            var target_server_list = MapHelper.GetBoundServerList(
                explore_target_info.object_info.map_id,
                Program.game_server_num,
                MapHelper.GetCell(position_key)
            );

            foreach (var target_server in target_server_list)
            {
                this.nats_client!.Publish(
                    MapHelper.GetUpdateExploreTargetSubject(
                        explore_target_info.object_info.map_id,
                        explore_target_info.object_info.map_sub_id,
                        target_server
                    ),
                    MessagePackSerializer.Serialize((position_key, explore_target_info))
                );
            }
        }

        public void BroadcastUpdateJobResourceInfo(JobResourceInfo job_resource_info)
        {
            var position_key = MapHelper.GetPositionKey(
                job_resource_info.object_info.map_id,
                job_resource_info.object_info.map_sub_id,
                job_resource_info.object_info.current_cell
            );

            var target_server_list = MapHelper.GetBoundServerList(
                job_resource_info.object_info.map_id,
                Program.game_server_num,
                MapHelper.GetCell(position_key)
            );

            foreach (var target_server in target_server_list)
            {
                this.nats_client!.Publish(
                    MapHelper.GetUpdateJobResourceSubject(
                        job_resource_info.object_info.map_id,
                        job_resource_info.object_info.map_sub_id,
                        target_server
                    ),
                    MessagePackSerializer.Serialize((position_key, job_resource_info))
                );
            }
        }

        public void SendToClient(Packet msg)
        {
            this.token.Send(msg);
            // TODO 이걸 ... Create랑 연계를 시켜야됨
            // Packet.Destroy(msg);
        }

        public void PublishToClients(Packet packet, List<long> user_id_list)
        {
            foreach (var user_id in user_id_list)
            {
                this.nats_client.Publish(
                    GameObjectInfo.MakeHashField(ObjectType.PLAYER, user_id),
                    packet.ToBytes()
                );
            }

            // TODO 이걸 ... Create랑 연계를 시켜야됨
            // Packet.Destroy(packet);
        }

        public async Task SendToGameServer(Packet msg)
        {
            await CacheHelper.Instance.EnqueueAsync("game_server_queue", msg.ToBytes());

            // TODO 이걸 ... Create랑 연계를 시켜야됨
            // Packet.Destroy(msg);
        }

        public async Task Release()
        {
            if (this.object_controller != null)
            {
                await this.object_controller.PublishDestroy();
                this.object_controller.HandleClientDisconnect();
            }

            if (current_progress_job != null)
            {
                await JobResourceInfo.Delete(current_progress_job.Value.Item2.resource_uid);
                JobController.BroadcastObjectDestroy(
                    this,
                    current_progress_job.Value.Item2.object_info
                );
            }

            await JobController.Decamp(this);

            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_G_LOGOUT(this.player_id);
                await this.SendToGameServer(packet);

                this.player_id = 0;
                this.nats_client.Close();
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

        public void RecvDuplicate()
        {
            Packet? packet = null;
            try
            {
                packet = Packet.Create((int)PROTOCOL.U_TO_U_DUPLICATE);
                this.SendToClient(packet);
                OnRemoved();
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

        public void OnRemoved()
        {
            this.cts!.Cancel();
            this.cts.Dispose();

            Program.leave_user_queue!.Enqueue(this);
            this.token.network_service.CloseClientSocket(this.token);
        }
    }
}
