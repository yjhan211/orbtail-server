namespace user_server
{
    using network;
    using MessagePack;
    using StackExchange.Redis;
    using game_server;
    using RedLockNet.SERedis;
    using network.Common;

    public partial class GameUser : IPeer
    {
        public UserToken token { get; private set; }
        public long player_id;
        public ConnectionMultiplexer redis_connection;
        public CacheHelper cache_helper { get; private set; }
        public RedLockFactory redlock { get; private set; }
        public SemaphoreSlim player_lock;

#pragma warning disable CS8618
        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.is_alive = true;
            this.token.is_released = false;
            this.token.SetPeer(this);

            this.player_id = 0;
            this.player_lock = new(1);
            this.object_lock = new(1);

            this.cts = new();
            this.move_object_queue = new();

            this.Initialize();
        }
#pragma warning restore

        public void Initialize()
        {
            this.redis_connection = RedisConnectionPool.GetConnection();
            this.cache_helper = new(this.redis_connection);

            this.redlock = RedisConnectionPool.GetRedLockFactory(this.redis_connection);
            this.nats_client = new(Program.nats_endpoint);

            this.move_object_task = Task.Run(RecvMoveObjectTask, cts.Token);
        }

        public async Task OnMessageFromClient(Const<byte[]> buffer)
        {
            try
            {
                byte[] clone = new byte[Config.BUFFER_SIZE];
                Array.Copy(buffer.Value, clone, buffer.Value.Length);
                if (Config.BUFFER_SIZE < buffer.Value.Length)
                {
                    throw new Exception(
                        $"Invalid Buffer Size. player id: {this.player_id}, size: {buffer.Value.Length}"
                    );
                }

                Packet packet = new(clone, this);
                PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
                long player_id = packet.PopPlayerId();
                byte[] body = packet.PopBody();

                switch (protocol_id)
                {
                    case PROTOCOL.HEART_BEAT:
                        await HeartBeat();
                        break;

                    case PROTOCOL.C_TO_U_LOGIN:
                        await this.player_lock.WaitAsync();
                        await HandleMessage<C_TO_U_LOGIN>(player_id, body, Login);
                        break;

                    default:
                        if (player_id == 0 || this.player_id != player_id)
                        {
                            throw new Exception($"Invalid ID: {this.player_id}, {player_id}");
                        }
                        switch (protocol_id)
                        {
                            case PROTOCOL.C_TO_U_MOVE:
                                await HandleMessage<C_TO_U_MOVE>(player_id, body, RequestMove);
                                break;

                            case PROTOCOL.C_TO_U_PLAYER_INFO:
                                await HandleMessage<C_TO_U_PLAYER_INFO>(
                                    player_id,
                                    body,
                                    GetPlayerInfo
                                );
                                break;

                            case PROTOCOL.C_TO_U_OBJECT_INFO:
                                await HandleMessage<C_TO_U_OBJECT_INFO>(
                                    player_id,
                                    body,
                                    GetObjectInfo
                                );
                                break;

                            case PROTOCOL.C_TO_U_GET_JOB:
                                await HandleMessage<C_TO_U_GET_JOB>(player_id, body, GetJob);
                                break;

                            case PROTOCOL.C_TO_U_CHAT_MSG:
                                LogManager.WriteInfoLog("보냄");
                                this.nats_client.Publish(
                                    "chat",
                                    MessagePackSerializer.Serialize((player_id, body))
                                );
                                break;
                        }
                        break;
                }
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

        async Task HandleMessage<T>(long player_id, byte[] body, Func<long, T, Task> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            await handleMessage(player_id, msg);
        }

        async Task HeartBeat()
        {
            this.token.is_alive = true;

            if (this.object_info == null)
            {
                return;
            }

            if (!this.object_info.current_cell.Equals(this.object_info.target_cell))
            {
                if (this.object_info.GetMoveElapsedTime() >= Config.MOVE_ELAPSED_TIME)
                {
                    await Move(this.object_info.target_cell, DirectionType.NONE);
                }
            }
        }

        async Task Login(long _, C_TO_U_LOGIN request)
        {
            if (this.player_id != 0)
            {
                throw new Exception("Already Has Player id");
            }

            long temp_player_id =
                request.account_token == "dummy"
                    ? await this.cache_helper.StringIncrement("temp_player_id")
                    : long.Parse(request.account_token);

            PlayerInfo? player_info = null;
            using (var player_lock = await PlayerController.Lock(this.redlock, this.player_id))
            {
                player_info = await PlayerController.Load(this.cache_helper, temp_player_id);

                if (player_info == null)
                {
                    // 플레이어 생성
                    player_info = new(
                        temp_player_id,
                        name: request.account_token == "dummy"
                            ? $"더미{temp_player_id}"
                            : $"플레이어{temp_player_id}",
                        MapHelper.GetRandomCell()
                    );
                }

                player_info.object_info.map_id = 1; // TODO 임시
                await PlayerController.Save(this.cache_helper, player_info);

                this.player_id = player_info.player_id;
            }

            this.object_info = player_info.object_info;
            this.last_cell = Cell.Clone(player_info.object_info.current_cell);

            // 개인 구독 시작
            this.nats_client.Subscribe(
                this.object_info.GetHashField(),
                async (channel, message) => await SubscribeToUser(message)
            );

            // 단체 구독 시작
            this.nats_client.Subscribe(
                "all",
                async (channel, message) => await SubscribeToUser(message)
            );

            // 계정 정보 전송
            Packet login_packet = PacketMaker.U_TO_C_LOGIN(player_info);
            SendToClient(login_packet);

            await Move(this.object_info.current_cell, DirectionType.NONE, true);

            // 이전 채팅기록 불러오기
            this.nats_client.Publish(
                $"chat_history",
                MessagePackSerializer.Serialize(this.object_info.GetHashField())
            );
        }

        async Task GetPlayerInfo(long player_id, C_TO_U_PLAYER_INFO body)
        {
            if (player_id != this.player_id)
            {
                return;
            }

            var player_id_list = body.player_id_list;
            var player_info_list = new List<PlayerInfo>();
            for (int i = 0; i < player_id_list.Count; i++)
            {
                var target_player_id = player_id_list[i];
                using (var player_lock = await PlayerController.Lock(this.redlock, this.player_id))
                {
                    var target_player_info = await PlayerController.Load(
                        this.cache_helper,
                        target_player_id
                    );

                    if (target_player_info == null)
                    {
                        continue;
                    }

                    player_info_list.Add(target_player_info);

                    bool is_max = player_info_list.Count >= Config.BROADCAST_UNIT;
                    bool is_ended = i == player_id_list.Count - 1;

                    if (is_max || is_ended)
                    {
                        Packet packet = PacketMaker.U_TO_C_PLAYER_INFO(player_info_list);
                        this.SendToClient(packet);

                        await Task.Delay(100);
                    }
                }
            }
        }

        // 이 함수가 호출되는 경우: G_TO_U_SPAWN_LIST의 object_key_list에는 있으나 클라에는 GameObjectInfo가 없을 때
        // 어떤 경우에 생기는가: 이미 접속해서 잠수타고 있는 오브젝트를 만났을 때
        async Task GetObjectInfo(long player_id, C_TO_U_OBJECT_INFO body)
        {
            if (player_id != this.player_id)
            {
                return;
            }

            RedisValue[] keys = body.object_key_list.ConvertAll(x => (RedisValue)x).ToArray();
            var object_info_list = await GameObjectController.LoadAll(this.cache_helper, keys);
            foreach (var object_info in object_info_list)
            {
                this.move_object_queue.Enqueue(object_info);
            }
        }

        async Task GetJob(long player_id, C_TO_U_GET_JOB body)
        {
            if (player_id != this.player_id)
            {
                return;
            }

            var job_info = await JobController.Load(this.cache_helper, this.player_id);
            if (job_info!.job_type != JobType.NONE)
            {
                this.SendToClient(
                    PacketMaker.U_TO_C_GET_JOB(this.player_id, ErrorCode.ALREADY_HAS_JOB, job_info)
                );
                return;
            }

            switch (body.job_type)
            {
                case JobType.GEOIOGIST:
                    job_info.job_type = JobType.GEOIOGIST;
                    job_info.job_grade = JobGrade.TRAINEE;
                    break;

                default:
                    break;
            }

            await JobController.Save(this.cache_helper, job_info);

            this.SendToClient(
                PacketMaker.U_TO_C_GET_JOB(this.player_id, ErrorCode.SUCCESS, job_info)
            );
        }

        public void SendToClient(Packet msg)
        {
            LogManager.WriteInfoLog("send chat 2");
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
            PublishDestroy();

            Packet packet = PacketMaker.U_TO_G_LOGOUT(this.player_id);
            _ = this.SendToGameServer(packet);

            await GameObjectController.Save(this.cache_helper, this.object_info);

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
