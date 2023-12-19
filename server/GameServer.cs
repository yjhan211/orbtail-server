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
            if (user.player_id > 0)
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

            user.player_id = temp_player_id;

            using (LockHelper.AcquireLock(PlayerInfo.GetLockKey(user.player_id)))
            { // 플레이어 생성
                PlayerInfo player_info =
                    new(
                        temp_player_id,
                        name: $"플레이어{temp_player_id}",
                        // new Cell(0, 0)
                        this.map_controller.GetRandomCell()
                    );

                player_info.object_info.map_id = MapController.MAP_ID;

                // 계정 정보 전송
                Packet login_packet = PacketMaker.MakeLoginPacket(player_info);
                user.Send(login_packet);

                // 월드에 게임 오브젝트 정보 갱신. 오브젝트 정보는 MovePlayer에서 별도로 보냄
                await this.map_controller.MovePlayer(user, player_info, DirectionType.TOP_LEFT);

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
            if (user.player_id <= 0)
            {
                return;
            }

            var player_info = await PlayerInfo.Load(user.player_id);
            if (player_info == null)
            {
                return;
            }

            await UpdatePosition(user, player_info);
        }

        async Task UpdatePosition(GameUser user, PlayerInfo player)
        {
            if (player.object_info.target_cell.Equals(player.object_info.current_cell))
            {
                return;
            }

            if (player.object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
            {
                return;
            }

            using (LockHelper.AcquireLock(PlayerInfo.GetLockKey(user.player_id)))
            {
                // 이동이 완료되었으면 포지션 업데이트
                await this.map_controller.MovePlayer(user, player, DirectionType.NONE);
            }
        }

        public async Task MovePlayer(GameUser user, DirectionType direction_type)
        {
            try
            {
                if (user.player_id <= 0)
                {
                    return;
                }

                PlayerInfo? player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    return;
                }

                // 아직 기존 이동이 완료되지 않았음. 무시
                if (player_info.object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
                {
                    return;
                }

                using (LockHelper.AcquireLock(PlayerInfo.GetLockKey(user.player_id)))
                {
                    await this.map_controller.MovePlayer(user, player_info, direction_type);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.StackTrace}, {e.Message}");
            }
        }

        public async Task GetPlayerInfo(GameUser user, List<long> target_player_id_list)
        {
            if (user.player_id <= 0)
            {
                return;
            }

            PlayerInfo? player_info = await PlayerInfo.Load(user.player_id);
            if (player_info == null)
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
            if (user.player_id <= 0)
            {
                return;
            }

            PlayerInfo? player_info = await PlayerInfo.Load(user.player_id);
            if (player_info == null)
            {
                return;
            }

            using (LockHelper.AcquireLock(PlayerInfo.GetLockKey(user.player_id)))
            {
                await this.map_controller.UnsetPlayer(player_info.object_info);
                // await player_info.Delete();

                // TODO 시스템메시지 분리
            }
        }
    }
}
