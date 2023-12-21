namespace game_server
{
    using System;
    using System.Threading.Tasks;
    using network;

    public partial class GameServer
    {
        readonly NetworkService network_service;
        readonly Dictionary<long, GameUser> user_map;
        readonly object operation_lock;
        readonly Queue<Packet> operation_queue;
        readonly Thread logic_thread;
        readonly AutoResetEvent loop_event;
        public MapController map_controller;

        public GameServer()
        {
            this.network_service = new();
            this.user_map = new();

            this.operation_lock = new();
            this.operation_queue = new();

            this.loop_event = new(false);
            this.logic_thread = new(GameLoop);

            this.map_controller = new();
        }

        public void Start()
        {
            PacketBufferManager.Initialize(Config.MAX_CONNECTION);
            this.network_service.Initialize();
            this.network_service.session_created_callback += (UserToken token) =>
            {
                GameUser user = new(token);
            };

            this.network_service.Listen();
            this.logic_thread.Start();

            Console.WriteLine("Game Server Start");
        }

        void GameLoop()
        {
            try
            {
                while (true)
                {
                    Packet? packet = null;

                    lock (this.operation_lock)
                    {
                        this.operation_queue.TryDequeue(out packet);
                    }

                    if (packet != null)
                    {
                        ProcessReceive(packet);
                    }

                    if (this.operation_queue.Count <= 0)
                    {
                        this.loop_event.WaitOne();
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.Message}, {e.StackTrace}");
            }
        }

        public void EnqueuePacket(Packet packet)
        {
            lock (this.operation_lock)
            {
                this.operation_queue.Enqueue(packet);
                this.loop_event.Set();
            }
        }

        static void ProcessReceive(Packet msg)
        {
            msg.owner.ProcessUserOperation(msg);
        }

        public async Task<PlayerInfo> LoginUserAsync(GameUser user)
        {
            if (user.player_info != null)
            {
                throw new Exception("Already Has Player id");
            }

            // TODO 로그인용 웹서버 생기면 거기서 토큰으로 유저아이디 불러오기
            // TODO 로그인용 웹서버는 데이터베이스에서 기존 토큰 있는지 확인 후 제거
            long temp_player_id = await CacheHelper.StringIncrement("temp_player_id");
            if (temp_player_id < 0 || !this.user_map.TryAdd(temp_player_id, user))
            {
                throw new Exception("Already Exist Player");
            }

            using (await LockHelper.AcquireLock(PlayerInfo.GetLockKey(temp_player_id)))
            { // 플레이어 생성
                PlayerInfo player_info =
                    new(
                        temp_player_id,
                        name: $"플레이어{temp_player_id}",
                        // new Cell(0, 0)
                        this.map_controller.GetRandomCell()
                    );

                player_info.object_info.map_id = MapController.MAP_ID;
                user.player_info = player_info;

                // 계정 정보 전송
                Packet login_packet = PacketMaker.MakeLoginPacket(player_info);
                user.Send(login_packet);

                // 월드에 게임 오브젝트 정보 갱신. 오브젝트 정보는 MovePlayer에서 별도로 보냄
                await this.map_controller.MovePlayer(user, DirectionType.NONE);

                // TODO 시스템메시지 분리
                return player_info;
            }
        }

        // public async Task SendChat(long player_id, string chat_message)
        // {
        //     // TODO 채팅 분리
        //     // if (!this.player_map.TryGetValue(player_id, out Player? player))
        //     // {
        //     //     return;
        //     // }
        // }

        public async Task HeartBeat(GameUser user)
        {
            await UpdatePosition(user);
        }

        async Task UpdatePosition(GameUser user)
        {
            if (user.player_info == null)
            {
                return;
            }

            GameObjectInfo object_info = user.player_info.object_info;
            if (object_info.target_cell.Equals(object_info.current_cell))
            {
                return;
            }

            // 아직 이동이 완료되지 않음
            if (object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
            {
                return;
            }

            await user.player_lock.WaitAsync();
            await this.map_controller.MovePlayer(user, DirectionType.NONE);
            user.player_lock.Release();
        }

        public async Task MovePlayer(GameUser user, DirectionType direction_type)
        {
            try
            {
                if (user.player_info == null)
                {
                    return;
                }

                // 아직 이동이 완료되지 않음
                if (user.player_info.object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
                {
                    return;
                }

                await this.map_controller.MovePlayer(user, direction_type);
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.StackTrace}, {e.Message}");
            }
        }

        public async Task GetPlayerInfo(GameUser user, List<long> target_player_id_list)
        {
            if (user.player_info == null)
            {
                return;
            }

            List<PlayerInfo> player_info_list = new();
            foreach (var target_player_id in target_player_id_list)
            {
                PlayerInfo? target_player_info = await PlayerInfo.Load(target_player_id);
                if (target_player_info == null)
                {
                    continue;
                }

                player_info_list.Add(target_player_info);
            }

            for (int i = 0; i < player_info_list.Count; i += Config.BROADCAST_UNIT)
            {
                List<PlayerInfo> chunk = player_info_list
                    .Skip(i)
                    .Take(Config.BROADCAST_UNIT)
                    .ToList();

                user.player_info_list_queue.Enqueue(chunk);
            }
        }

        public async Task LeavePlayer(GameUser user)
        {
            if (user.player_info == null)
            {
                return;
            }

            using (await LockHelper.AcquireLock(PlayerInfo.GetLockKey(user.player_info.player_id)))
            {
                await this.map_controller.UnsetPlayer(user.player_info.object_info);
                // await player_info.Delete();

                // TODO 시스템메시지 분리
            }
        }
    }
}
