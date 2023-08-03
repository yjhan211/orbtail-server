namespace game_server
{
    using System;
    using System.Collections.Concurrent;
    using System.Runtime.InteropServices;
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

        readonly Player?[,] player_cell_info;
        const int MAP_SIZE = 100;

        public GameServer()
        {
            this.network_service = new();

            this.player_map_lock = new();
            this.player_map = new();

            this.operation_lock = new();
            this.operation_queue = new();

            this.loop_event = new(false);
            this.logic_thread = new(GameLoop);

            this.player_cell_info = new Player?[MAP_SIZE, MAP_SIZE];
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

        static int latest_player_id = 0;

        public long LoginUser(GameUser user)
        {
            Player player;

            lock (this.player_map_lock)
            {
                // 플레이어 생성
                Interlocked.Increment(ref latest_player_id);
                player = new(user, latest_player_id, name: $"플레이어{latest_player_id}");

                List<Player?> spawn_broadcast_target = GetBroadCastTarget(player.current_cell);
                foreach (Player? target_player in spawn_broadcast_target)
                {
                    if (target_player == null)
                    {
                        continue;
                    }

                    // 기존 영역 유저들에게 새 유저 정보 전송
                    target_player.owner.packet_queue_processor.AddToQueue(
                        PROTOCOL.S_TO_C_PLAYER_SPAWN_LIST,
                        player.ConvertObj()
                    );

                    // 새 유저에게 기존 영역 유저 정보 전송
                    player.owner.packet_queue_processor.AddToQueue(
                        PROTOCOL.S_TO_C_PLAYER_SPAWN_LIST,
                        target_player.ConvertObj()
                    );
                }

                // 본인 정보 전송
                Packet login_packet = MakeLoginPacket(player);
                user.Send(login_packet);

                // 새 플레이어를 월드에 등록
                this.player_map.Add(player.player_id, player);

                // TODO 중복 등록 처리 - Player가 아니라 List<GameObject>로 개선
                this.player_cell_info[player.current_cell.x, player.current_cell.y] = player;

                // DrawPlayerCellInfo();

                // TODO 시스템메시지 분리
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

            // TODO 채팅 분리
        }

        public void HeartBeat(long player_id)
        {
            lock (this.player_map_lock)
            {
                if (!this.player_map.TryGetValue(player_id, out Player? player))
                {
                    return;
                }

                #region 현재 위치 조정
                if (GetMoveElapsedTime(player) < Config.MOVE_ELAPSED_TIME)
                {
                    return;
                }

                if (player.current_cell.Equals(player.target_cell))
                {
                    return;
                }

                UpdatePosition(player);
                #endregion
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

                if (GetMoveElapsedTime(player) <= Config.MOVE_ELAPSED_TIME)
                {
                    return;
                }

                UpdatePosition(player);

                (CellPosition? temp_target_cell, player.is_flip) = CalcTargetPosition(
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

            MoveObj move_obj =
                new()
                {
                    player_id = player.player_id,
                    current_cell = player.current_cell,
                    target_cell = player.target_cell,
                    move_timestamp = player.move_timestamp,
                    is_flip = player.is_flip,
                };

            List<Player?> move_broadcast_target = GetBroadCastTarget(player.current_cell);
            foreach (Player? target_player in move_broadcast_target)
            {
                if (target_player == null)
                {
                    continue;
                }

                target_player.owner.packet_queue_processor.AddToQueue(
                    PROTOCOL.S_TO_C_MOVE_LIST,
                    move_obj
                );
            }
        }

        public void UpdatePosition(Player player)
        {
            CellPosition latest_position = new(player.current_cell.x, player.current_cell.y);
            this.player_cell_info[latest_position.x, latest_position.y] = null;

            player.current_cell = new(player.target_cell.x, player.target_cell.y);
            this.player_cell_info[player.current_cell.x, player.current_cell.y] = player;

            // DrawPlayerCellInfo();
        }

        (CellPosition?, bool) CalcTargetPosition(CellPosition current_cell, DirectionType direction)
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

            if (IsOutOfMapRange(current_cell))
            {
                return (null, is_flip);
            }

            if (HasPlayer(current_cell))
            {
                return (null, is_flip);
            }

            return (current_cell, is_flip);
        }

        bool HasPlayer(CellPosition cell)
        {
            return this.player_cell_info[cell.x, cell.y] != null;
        }

        bool IsOutOfMapRange(CellPosition cell)
        {
            return cell.x < 0 || cell.x >= MAP_SIZE || cell.y < 0 || cell.y >= MAP_SIZE;
        }

        double GetMoveElapsedTime(Player player)
        {
            TimeSpan elapsedTime = DateTime.UtcNow - player.move_timestamp;
            return elapsedTime.TotalSeconds;
        }

        public void LeaveUser(long player_id)
        {
            // TODO 시스템메시지 분리
            // SendChat(player_id, $"님이 접속 종료하였습니다.");

            lock (this.player_map_lock)
            {
                if (!this.player_map.TryGetValue(player_id, out Player? player))
                {
                    return;
                }

                this.player_cell_info[player.current_cell.x, player.current_cell.y] = null;
                this.player_map.Remove(player_id);

                List<Player?> destroy_broadcast_target = GetBroadCastTarget(player.current_cell);
                foreach (Player? target_player in destroy_broadcast_target)
                {
                    if (target_player == null)
                    {
                        continue;
                    }

                    target_player.owner.packet_queue_processor.AddToQueue(
                        PROTOCOL.S_TO_C_PLAYER_DESTROY_LIST,
                        player.ConvertObj()
                    );
                }
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

        List<Player?> GetBroadCastTarget(CellPosition owner_position)
        {
            return new List<Player?>(this.player_map.Values);

            List<Player?> target_list = new();

            const int X_MIN_BOUND = -11;
            const int X_MAX_BOUND = 14;

            const int Y_MIN_BOUND = -5;
            const int Y_MAX_BOUND = -6;

            var min_x = owner_position.x + X_MIN_BOUND;
            var max_x = owner_position.x + X_MAX_BOUND;

            var min_y = owner_position.y + Y_MIN_BOUND;
            var max_y = owner_position.y + Y_MAX_BOUND;

            var line = 0;

            // Console.WriteLine($"owner: {owner_position.x}, {owner_position.y}");
            for (int x = min_x; x <= max_x; x++)
            {
                line += 1;

                if (line <= 6)
                {
                    min_y -= 1;
                }
                else if (7 < line)
                {
                    min_y += 1;
                }

                if (line <= 20)
                {
                    max_y += 1;
                }
                else if (21 < line)
                {
                    max_y -= 1;
                }

                // Console.Write($"[{line}]");

                for (int y = min_y; y <= max_y; y++)
                {
                    if (IsOutOfMapRange(new CellPosition(x, y)))
                    {
                        continue;
                    }

                    // Console.Write($"({x},{y})");

                    if (!HasPlayer(new CellPosition(x, y)))
                    {
                        continue;
                    }

                    target_list.Add(this.player_cell_info[x, y]);
                }

                // Console.WriteLine("");
            }

            return target_list;
        }

        public void DrawPlayerCellInfo()
        {
            for (int x = MAP_SIZE - 1; x >= 0; x--)
            {
                for (int y = MAP_SIZE - 1; y >= 0; y--)
                {
                    if (this.player_cell_info[x, y] == null)
                    {
                        Console.Write("X,");
                    }
                    else
                    {
                        Console.Write("O,");
                    }
                }
                Console.WriteLine("");
            }

            Console.WriteLine("==========================================");
        }

        static void ProcessReceive(Packet msg)
        {
            msg.owner.ProcessUserOperation(msg);
        }
    }
}
