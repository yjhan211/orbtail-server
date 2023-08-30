namespace game_server
{
    using System;
    using System.Collections.Concurrent;
    using network;

    public partial class GameServer
    {
        readonly NetworkService network_service;
        readonly object world_lock;
        readonly Dictionary<long, Player> player_map;

        readonly object operation_lock;
        readonly Queue<Packet> operation_queue;

        readonly Thread logic_thread;
        readonly AutoResetEvent loop_event;

        readonly List<GameObject>[,] world_info;
        const int MAP_SIZE = 10;
        static long latest_player_id = 0;

        public GameServer()
        {
            this.network_service = new();

            this.world_lock = new();
            this.player_map = new();

            this.operation_lock = new();
            this.operation_queue = new();

            this.loop_event = new(false);
            this.logic_thread = new(GameLoop);

            this.world_info = new List<GameObject>[MAP_SIZE, MAP_SIZE];
            for (int x = 0; x < MAP_SIZE; x++)
            {
                for (int y = 0; y < MAP_SIZE; y++)
                {
                    this.world_info[x, y] = new();
                    Console.WriteLine(this.world_info[x, y].Count);
                }
            }

            Console.WriteLine("==================================================");
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

        public long LoginUser(GameUser user)
        {
            Player player;

            lock (this.world_lock)
            {
                // 플레이어 생성
                Interlocked.Increment(ref latest_player_id);
                player = new(user, latest_player_id, name: $"플레이어{latest_player_id}");

                // 본인 정보 전송
                Packet login_packet = MakeLoginPacket(player);
                user.Send(login_packet);

                // 새 플레이어를 월드에 등록
                this.player_map.Add(player.object_id, player);
                MoveFinish(player, player.target_cell);

                // TODO 시스템메시지 분리
            }

            return player.object_id;
        }

        public void SendChat(long player_id, string chat_message)
        {
            if (!this.player_map.TryGetValue(player_id, out Player? player))
            {
                return;
            }

            // TODO 채팅 분리
        }

        public void HeartBeat(long player_id)
        {
            lock (this.world_lock)
            {
                if (!this.player_map.TryGetValue(player_id, out Player? player))
                {
                    return;
                }

                UpdatePosition(player);
            }
        }

        public void UpdatePosition(Player player)
        {
            if (GetMoveElapsedTime(player) < Config.MOVE_ELAPSED_TIME)
            {
                return;
            }

            lock (this.world_lock)
            {
                MoveFinish(player, player.target_cell);
            }
        }

        public void MovePlayer(long player_id, DirectionType direction)
        {
            lock (this.world_lock)
            {
                if (!this.player_map.TryGetValue(player_id, out Player? player))
                {
                    return;
                }

                if (GetMoveElapsedTime(player) <= Config.MOVE_ELAPSED_TIME)
                {
                    return;
                }

                MoveFinish(player, player.target_cell);

                (CellPosition? temp_target_cell, player.is_flip) = CalcMoveTarget(
                    new(player.current_cell.x, player.current_cell.y),
                    direction
                );

                if (temp_target_cell == null || player.target_cell.Equals(temp_target_cell))
                {
                    return;
                }

                player.target_cell = temp_target_cell;
                player.move_timestamp = DateTime.UtcNow;
            }
        }

        public void LeaveUser(long player_id)
        {
            // TODO 시스템메시지 분리
            lock (this.world_lock)
            {
                if (!this.player_map.TryGetValue(player_id, out Player? player))
                {
                    return;
                }

                RemoveInMap(player);
                this.player_map.Remove(player_id);
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
    }
}
