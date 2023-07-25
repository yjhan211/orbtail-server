namespace game_server
{
    using network;

    public partial class GameServer
    {
        readonly NetworkService network_service;
        readonly object player_map_lock;
        readonly Dictionary<long, Player> player_map;
        readonly object operation_lock;
        readonly Queue<Packet> operation_queue;
        readonly Thread logic_thread;
        readonly AutoResetEvent loop_event;
        readonly Queue<S_TO_C_MOVE_ALL> player_move_queue;

        // readonly MapTile[,] map_info;

        public GameServer()
        {
            this.network_service = new();

            this.player_map_lock = new();
            this.player_map = new();

            this.operation_lock = new();
            this.operation_queue = new();

            this.loop_event = new(false);
            this.logic_thread = new(GameLoop);

            this.player_move_queue = new();

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

            Thread move_broadcast_thread =
                new(() =>
                {
                    while (true)
                    {
                        List<S_TO_C_MOVE_ALL> move_list = new();

                        while (true)
                        {
                            if (this.player_move_queue.Count <= 0)
                            {
                                break;
                            }

                            if (move_list.Count > 10)
                            {
                                break;
                            }

                            if (this.player_move_queue.TryDequeue(out S_TO_C_MOVE_ALL? move_obj))
                            {
                                move_list.Add(move_obj);
                            }
                        }

                        if (move_list.Count > 0)
                        {
                            Packet packet = MakeMoveListPacket(move_list);
                            Broadcast(packet);
                        }

                        Thread.Sleep(10);
                    }
                });

            move_broadcast_thread.Start();
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

        public long LoginUser(GameUser user)
        {
            Player player;

            lock (this.player_map_lock)
            {
                // 플레이어 생성
                Interlocked.Increment(ref latest_player_id);
                player = new(user, latest_player_id, name: $"플레이어{latest_player_id}");

                // 기존 월드 유저들에게 로그인 정보 전송
                Packet broadcast_packet = MakeSpawnPlayerPacket(player);
                Program.game_server.Broadcast(broadcast_packet);

                // 본인 정보 전송
                Packet login_packet = MakeLoginPacket(player);
                user.Send(login_packet);

                // 기존 월드 유저 정보 전송
                foreach (KeyValuePair<long, Player> pair in this.player_map)
                {
                    Packet spawn_player_packet = MakeSpawnPlayerPacket(pair.Value);
                    user.Send(spawn_player_packet);
                }

                // 새 플레이어를 월드에 등록
                this.player_map.Add(player.player_id, player);

                // TODO 시스템메시지 분리
                SendChat(player.player_id, $"님이 접속하였습니다.");
            }

            return player.player_id;
        }

#pragma warning disable CA1822
        public void SendChat(long player_id, string chat_message)
        {
            if (!this.player_map.TryGetValue(player_id, out Player? player))
            {
                return;
            }

            Packet packet = MakeChatPacket(player, chat_message);
            Program.game_server.Broadcast(packet);
        }

        public void HeartBeat(long player_id)
        {
            lock (this.player_map_lock)
            {
                if (!this.player_map.TryGetValue(player_id, out Player? player))
                {
                    return;
                }

                if (GetMoveElapsedTime(player) < Config.MOVE_ELAPSED_TIME)
                {
                    return;
                }

                player.current_cell = CellPosition.Clone(player.target_cell);
            }
        }

        public void Move(long player_id, DirectionType direction)
        {
            Player? player;

            lock (this.player_map_lock)
            {
                if (!this.player_map.TryGetValue(player_id, out player))
                {
                    return;
                }

                if ((float)GetMoveElapsedTime(player) <= Config.MOVE_ELAPSED_TIME)
                {
                    return;
                }

                player.current_cell = CellPosition.Clone(player.target_cell);
                (CellPosition temp_target_cell, player.is_flip) = CalcTargetPosition(
                    player.current_cell,
                    direction
                );

                if (player.target_cell.Equals(temp_target_cell))
                {
                    return;
                }

                player.target_cell = temp_target_cell;
                player.move_timestamp = DateTime.UtcNow;
            }

            S_TO_C_MOVE_ALL move_obj =
                new()
                {
                    player_id = player.player_id,
                    current_cell = player.current_cell,
                    target_cell = player.target_cell,
                    move_timestamp = player.move_timestamp,
                    is_flip = player.is_flip,
                };

            this.player_move_queue.Enqueue(move_obj);
        }

        static (CellPosition, bool) CalcTargetPosition(
            CellPosition current_cell,
            DirectionType direction
        )
        {
            bool is_flip = false;

            if (direction == DirectionType.TOP_LEFT)
            {
                current_cell.x += 1;
                is_flip = true;
            }
            else if (direction == DirectionType.TOP_RIGHT)
            {
                current_cell.x -= 1;
            }
            else if (direction == DirectionType.BOTTOM_LEFT)
            {
                current_cell.y -= 1;
                is_flip = true;
            }
            else
            {
                current_cell.y += 1;
            }

            return (current_cell, is_flip);
        }

        double GetMoveElapsedTime(Player player)
        {
            TimeSpan elapsedTime = DateTime.UtcNow - player.move_timestamp;
            return elapsedTime.TotalSeconds;
        }

        public void LeaveUser(long player_id)
        {
            if (!this.player_map.TryGetValue(player_id, out Player? player))
            {
                return;
            }

            SendChat(player_id, $"님이 접속 종료하였습니다.");

            lock (this.player_map_lock)
            {
                this.player_map.Remove(player_id);
            }

            Packet packet = MakeDistroyPlayerPacket(player);
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
                foreach (KeyValuePair<long, Player> pair in this.player_map)
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
