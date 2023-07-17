namespace game_server
{
    using network;

    public partial class GameServer
    {
        readonly NetworkService network_service;
        readonly object player_map_lock;
        readonly Dictionary<int, Player> player_map;
        readonly object operation_lock;
        readonly Queue<Packet> operation_queue;
        readonly Thread logic_thread;
        readonly AutoResetEvent loop_event;
        readonly MapTile[,] map_info;

        public GameServer()
        {
            this.network_service = new();

            this.player_map_lock = new();
            this.player_map = new Dictionary<int, Player>();

            this.operation_lock = new object();
            this.operation_queue = new Queue<Packet>();

            this.loop_event = new AutoResetEvent(false);
            this.logic_thread = new Thread(GameLoop);

            // this.map_info = new MapTile[100, 100];

            // for (int x = 0; x < 100; x++)
            // {
            //     for (int y = 0; y < 100; y++)
            //     {
            //         this.map_info[x, y] = new MapTile(TileType.SIDEWALK);
            //         Console.Write((int)this.map_info[x, y].type);
            //         Console.Write(",");
            //     }
            //     Console.WriteLine("");
            // }
        }

        public void Start()
        {
            PacketBufferManager.Initialize(2000);
            this.network_service.Initialize();
            this.network_service.session_created_callback += (UserToken token) =>
            {
                // TODO DB 붙이기 전까진 일단 이렇게 ...
                GameUser user = new(token);
            };

            this.network_service.Listen();
            this.logic_thread.Start();

            Console.WriteLine("Game Server Start");
        }

        void GameLoop()
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

        static int latest_player_id = 0;

        public (Player player, List<PlayerObj> player_list) LoginUser(GameUser user)
        {
            List<PlayerObj> player_list = new();

            lock (this.player_map_lock)
            {
                foreach (KeyValuePair<int, Player> pair in this.player_map)
                {
                    player_list.Add(pair.Value.ConvertObj());
                }
            }

            // 플레이어 생성
            Interlocked.Increment(ref latest_player_id);
            Player player = new(user, latest_player_id, name: $"플레이어{latest_player_id}");

            // 기존 월드 유저들에게 로그인 정보 전송
            Packet broadcast_packet = MakeLoginAllPacket(player);
            Program.game_server.Broadcast(broadcast_packet);

            // 새 플레이어를 월드에 등록
            lock (this.player_map_lock)
            {
                this.player_map.Add(player.player_id, player);
            }

            // 월드 정보 동적으로 로드하도록 개선해야 함 (1024바이트 이하로 쪼개서..)
            return (player, player_list);
        }

        public static void SendChat(Player player, string chat_message)
        {
            Packet packet = MakeChatPacket(player, chat_message);
            Program.game_server.Broadcast(packet);
        }

        public static void SendMove() { }

        public void LeaveUser(Player player)
        {
            if (player == null)
            {
                return;
            }

            lock (this.player_map_lock)
            {
                this.player_map.Remove(player.player_id);
            }

            Packet packet = MakeLogoutAllPacket(player);
            Program.game_server.Broadcast(packet);
        }

        public void EnqueuePacket(Packet packet)
        {
            lock (this.operation_lock)
            {
                this.operation_queue.Enqueue(packet);
                this.loop_event.Set();
            }
        }

        public void Broadcast(Packet msg)
        {
            Packet clone = new();
            msg.CopyTo(clone);

            lock (this.player_map_lock)
            {
                foreach (KeyValuePair<int, Player> pair in this.player_map)
                {
                    pair.Value.Send(clone, true);
                }
            }

            Packet.Destroy(msg);
        }

        static void ProcessReceive(Packet msg)
        {
            msg.owner.ProcessUserOperation(msg);
        }
    }
}
