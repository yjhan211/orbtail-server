namespace user_server
{
    using network;
    using MessagePack;
    using StackExchange.Redis;
    using game_server;
    using RedLockNet.SERedis;
    using log4net.Repository.Hierarchy;

    public class GameUser : IPeer
    {
        public UserToken token { get; private set; }
        public ConnectionMultiplexer redis_connection { get; private set; }
        public CacheHelper cache_helper { get; private set; }
        public RedLockFactory redlock { get; private set; }
        public SemaphoreSlim player_lock { get; private set; }
        public CancellationTokenSource cts { get; private set; }
        public NatsClient nats_client { get; private set; }

        /*-------------------------------------------------------------*/

        public long player_id { get; private set; }
        GameObjectController? object_controller { get; set; }

        /*-------------------------------------------------------------*/

        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.is_alive = true;
            this.token.is_released = false;
            this.token.SetPeer(this);

            this.redis_connection = RedisConnectionPool.GetConnection();
            this.cache_helper = new(this.redis_connection);
            this.redlock = RedisConnectionPool.GetRedLockFactory(this.redis_connection);

            this.nats_client = new(Program.nats_endpoint);

            this.player_id = 0;
            this.player_lock = new(1);

            this.cts = new();
        }

        async Task HandleMessage<T>(byte[] body, Func<GameUser, T, Task> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            await handleMessage(this, msg);
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

                await this.player_lock.WaitAsync();

                switch (protocol_id)
                {
                    case PROTOCOL.HEART_BEAT:
                        await HeartBeat();
                        break;

                    case PROTOCOL.C_TO_U_LOGIN:
                        await HandleMessage<C_TO_U_LOGIN>(body, Login);
                        break;

                    default:
                        if (player_id == 0 || this.player_id != player_id)
                        {
                            throw new Exception($"Invalid ID: {this.player_id}, {player_id}");
                        }
                        if (this.object_controller == null)
                        {
                            return;
                        }
                        switch (protocol_id)
                        {
                            case PROTOCOL.C_TO_U_CHANGE_MAP_SUCCESS:
                                await ChangeMapSuccess();
                                break;

                            case PROTOCOL.C_TO_U_CHAT_LOG:
                                await ChatLog();
                                break;

                            case PROTOCOL.C_TO_U_MOVE:
                                await HandleMessage<C_TO_U_MOVE>(
                                    body,
                                    this.object_controller.RequestMove
                                );
                                break;

                            case PROTOCOL.C_TO_U_PLAYER_INFO:
                                await HandleMessage<C_TO_U_PLAYER_INFO>(body, GetPlayerInfo);
                                break;

                            case PROTOCOL.C_TO_U_OBJECT_INFO:
                                await HandleMessage<C_TO_U_OBJECT_INFO>(
                                    body,
                                    this.object_controller.GetObjectInfo
                                );
                                break;

                            case PROTOCOL.C_TO_U_JOB_RESOURCE_INFO:
                                await HandleMessage<C_TO_U_JOB_RESOURCE_INFO>(
                                    body,
                                    GetJobResourceInfo
                                );
                                break;

                            case PROTOCOL.C_TO_U_GET_JOB:
                                await HandleMessage<C_TO_U_GET_JOB>(body, JobController.GetJob);
                                break;

                            case PROTOCOL.C_TO_U_WEAR_ITEM:
                                await HandleMessage<C_TO_U_WEAR_ITEM>(
                                    body,
                                    InventoryController.RequestWearItem
                                );
                                break;

                            case PROTOCOL.C_TO_U_USE_SKILL:
                                await HandleMessage<C_TO_U_USE_SKILL>(
                                    body,
                                    JobController.UseJobSkill
                                );
                                break;

                            case PROTOCOL.C_TO_U_CHAT_MSG:
                                await HandleMessage<C_TO_U_CHAT_MSG>(body, ChatController.SendChat);
                                break;
                        }
                        break;
                }

                Packet.Destroy(packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                this.OnRemoved();
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
                }

                Packet.Destroy(packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                this.OnRemoved();
            }
        }

        async Task HeartBeat()
        {
            this.token.is_alive = true;

            if (this.object_controller != null)
            {
                await this.object_controller.HeartBeat();
            }
        }

        async Task Login(GameUser _, C_TO_U_LOGIN request)
        {
            if (this.player_id != 0)
            {
                throw new Exception("Already Has Player id");
            }

            long temp_player_id =
                request.account_token == "dummy"
                    ? await cache_helper.StringIncrement("temp_player_id")
                    : long.Parse(request.account_token);

            bool is_new = false;
            PlayerInfo? player_info = null;
            using (await PlayerInfoController.Lock(this.redlock, this.player_id))
            {
                player_info = await PlayerInfoController.Load(cache_helper, temp_player_id);
                if (player_info == null)
                {
                    // 플레이어 생성
                    player_info = new(
                        temp_player_id,
                        name: request.account_token == "dummy"
                            ? $"더미{temp_player_id}"
                            : $"플레이어{temp_player_id}",
                        new(90, 140)
                    );

                    is_new = true;
                    player_info.object_info.map_id = MapID.CITY_1;
                }
                await PlayerInfoController.Save(this.cache_helper, player_info);

                this.player_id = player_info.player_id;
                this.object_controller = new(this, player_info.object_info);

                if (is_new)
                {
                    // 기본 아이템 증정
                    var default_hair = await InventoryController.CreateItem(this, 1001000001, 1);

                    await InventoryController.AddItem(this, this.player_id, default_hair);
                    player_info = await InventoryController.WearItem(this, default_hair.item_uid);
                }
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

            // 계정 정보 전송
            Packet login_packet = PacketMaker.U_TO_C_LOGIN(player_info);
            SendToClient(login_packet);
        }

        async Task ChangeMapSuccess()
        {
            await this.object_controller!.Move(
                this.object_controller.object_info.current_cell,
                DirectionType.NONE,
                true
            );
        }

        async Task ChatLog()
        {
            // 이전 채팅기록 불러오기
            var chat_history = await ChatController.GetChatHistory(this, ChatType.ALL);
            foreach (var chat_packet in chat_history)
            {
                SendToClient(chat_packet);
            }
        }

        async Task GetPlayerInfo(GameUser _, C_TO_U_PLAYER_INFO body)
        {
            var player_id_list = body.player_id_list;

            RedisValue[] keys = body.player_id_list.ConvertAll(x => (RedisValue)x).ToArray();
            var player_info_list = await PlayerInfoController.LoadAll(cache_helper, keys);

            Packet packet = PacketMaker.U_TO_C_PLAYER_INFO(player_info_list);
            this.SendToClient(packet);
        }

        async Task GetJobResourceInfo(GameUser _, C_TO_U_JOB_RESOURCE_INFO body)
        {
            var job_resource_id_list = body.job_resource_id_list;
            var job_resource_info_list = new List<JobResourceInfo>();

            for (int i = 0; i < job_resource_id_list.Count; i++)
            {
                var target_resource_id = job_resource_id_list[i];
                JobResourceInfo? target_resource_info;

                using (await JobResourceController.Lock(this.redlock, target_resource_id))
                {
                    target_resource_info = await JobResourceController.Load(
                        cache_helper,
                        target_resource_id
                    );
                }

                if (target_resource_info == null)
                {
                    continue;
                }

                job_resource_info_list.Add(target_resource_info);

                bool is_max = job_resource_id_list.Count >= Config.BROADCAST_UNIT;
                bool is_ended = i == job_resource_id_list.Count - 1;

                if (is_max || is_ended)
                {
                    Packet packet = PacketMaker.U_TO_C_JOB_RESOURCE_INFO(job_resource_info_list);
                    this.SendToClient(packet);

                    await Task.Delay(100);
                }
            }
        }

        void SubscribePlayerInfo(GameUser _, G_TO_U_PLAYER_INFO body)
        {
            var player_info_list = new List<PlayerInfo> { body.player_info };

            Packet packet = PacketMaker.U_TO_C_PLAYER_INFO(player_info_list);
            this.SendToClient(packet);
        }

        void SubscribeChatMsg(GameUser _, U_TO_C_CHAT_MSG body)
        {
            Packet packet = PacketMaker.U_TO_C_CHAT_MSG(
                body.chat_type,
                body.name,
                body.chat_message
            );

            this.SendToClient(packet);
        }

        public void SendToClient(Packet msg)
        {
            this.token.Send(msg);
            Packet.Destroy(msg);
        }

        public async Task SendToGameServer(Packet msg)
        {
            await this.cache_helper.Enqueue("game_server_queue", msg.ToBytes());
            Packet.Destroy(msg);
        }

        public async Task Release()
        {
            if (this.object_controller != null)
            {
                await this.object_controller.PublishDestroy();
            }

            Packet packet = PacketMaker.U_TO_G_LOGOUT(this.player_id);
            _ = this.SendToGameServer(packet);

            this.player_id = 0;
            this.nats_client.Close();
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
