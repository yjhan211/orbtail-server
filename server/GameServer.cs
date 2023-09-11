namespace game_server
{
    using System;
    using System.Collections.Concurrent;
    using network;

    public partial class GameServer
    {
        readonly NetworkService network_service;
        public object world_lock;
        readonly Dictionary<long, Player> player_map;
        readonly object operation_lock;
        readonly Queue<Packet> operation_queue;
        readonly Thread logic_thread;
        readonly AutoResetEvent loop_event;
        static long latest_player_id = 0;
        public MapController map_controller;

        public GameServer()
        {
            this.network_service = new();

            this.world_lock = new();
            this.player_map = new();

            this.operation_lock = new();
            this.operation_queue = new();

            this.loop_event = new(false);
            this.logic_thread = new(GameLoop);

            this.map_controller = new();
        }

        public void Start()
        {
            PacketBufferManager.Initialize(10000);
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

        public long LoginUser(GameUser user)
        {
            Player player;

            lock (this.world_lock)
            {
                // 플레이어 생성
                Interlocked.Increment(ref latest_player_id);
                player = new(user, latest_player_id, name: $"플레이어{latest_player_id}");

                // 새 플레이어를 월드에 등록
                if (!this.player_map.TryAdd(player.object_id, player))
                {
                    throw new Exception("Already Exist Player");
                }

                // 본인 정보 전송
                Packet login_packet = MakeLoginPacket(player);
                user.Send(login_packet);

                this.map_controller.SpawnGameObject(player);

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
            if (!this.player_map.TryGetValue(player_id, out Player? player))
            {
                return;
            }

            UpdatePosition(player);
        }

        void UpdatePosition(Player player)
        {
            if (player.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
            {
                return;
            }

            this.map_controller.MoveGameObject(player);
        }

        public void MovePlayer(long player_id, DirectionType direction)
        {
            if (!this.player_map.TryGetValue(player_id, out Player? player))
            {
                return;
            }

            if (player.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
            {
                return;
            }

            this.map_controller.MoveGameObject(player);

            CellPosition current_cell = new(player.current_cell.x, player.current_cell.y);
            this.map_controller.SetPlayerTargetCell(player, current_cell, direction);

            player.SetFlip(direction);
        }

        public void LeaveUser(long player_id)
        {
            lock (this.world_lock)
            {
                if (!this.player_map.TryGetValue(player_id, out Player? player))
                {
                    return;
                }

                player.cts.Cancel();

                this.map_controller.ReleaseGameObject(player);
                this.player_map.Remove(player_id);
            }

            // TODO 시스템메시지 분리
        }
    }
}
