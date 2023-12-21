#pragma warning disable IDE1006

namespace game_server
{
    using network;
    using MessagePack;
    using StackExchange.Redis;
    using System.Text.Json;
    using System.Collections.Concurrent;

    public class GameUser : IPeer
    {
        public UserToken token { get; private set; }
        public object player_info_lock { get; private set; }
        public PlayerInfo? player_info { get; set; }
        public CancellationTokenSource cts;
        public ConcurrentQueue<GameObjectInfo> move_queue;
        public ConcurrentQueue<List<PlayerInfo>> player_info_list_queue;
        public string? move_channel;
        Task? world_info_task;
        Task world_update_task;
        Task game_object_info_task;

        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.is_alive = true;
            this.token.is_released = false;
            this.token.SetPeer(this);

            this.player_info_lock = new();
            this.cts = new();

            this.move_queue = new();
            this.player_info_list_queue = new();

            this.move_channel = default;

            // this.world_info_task = Task.Run(RecvWorldInfo, cts.Token);
            this.world_update_task = Task.Run(RecvSubscribed, cts.Token);
            this.game_object_info_task = Task.Run(RecvGameObjectInfo, cts.Token);
        }

        string GetPositionKey(int map_id, Cell cell)
        {
            return $"map_{map_id}|{cell.x},{cell.y}";
        }

        public void HandleMoveMessage(GameObjectInfo info)
        {
            this.move_queue.Enqueue(info);
        }

        async Task RecvWorldInfo()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (this.player_info == null)
                    {
                        continue;
                    }

                    List<string> bound_cell_list;
                    lock (this.player_info_lock)
                    {
                        bound_cell_list = MapController
                            .GetBoundCellList(player_info.object_info.current_cell)
                            .Select((cell) => GetPositionKey(player_info.object_info.map_id, cell))
                            .ToList();
                    }

                    var game_object_keys = await CacheHelper.ListRange(bound_cell_list);
                    var game_object_list = await GameObjectInfo.LoadAll(game_object_keys);

                    for (int i = 0; i < game_object_list.Count; i += Config.BROADCAST_UNIT)
                    {
                        var batch = game_object_list.Skip(i).Take(Config.BROADCAST_UNIT).ToList();
                        var is_ended = (i + Config.BROADCAST_UNIT) >= game_object_list.Count;

                        Packet packet = PacketMaker.MakeMapInfoPacket(batch, is_ended);
                        this.Send(packet);

                        await Task.Delay(100);
                    }

                    await Task.Delay(1000);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.StackTrace} || {e.Message}");
                    await this.OnRemoved();
                }
            }

            try
            {
                // this.world_info_task.Wait();
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"{e.StackTrace} || {e.Message}");
            }
        }

        async Task RecvSubscribed()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (this.player_info == null)
                    {
                        continue;
                    }

                    List<GameObjectInfo> game_object_list = new();

                    // move_queue에 데이터가 없을 때까지 기다리고 비동기적으로 처리
                    while (!this.move_queue.Any())
                    {
                        await Task.Delay(200); // 200ms 대기
                    }

                    // move_queue에 데이터가 있을 때 리스트에 추가
                    while (this.move_queue.TryPeek(out var object_info))
                    {
                        if (game_object_list.Count >= Config.BROADCAST_UNIT)
                        {
                            break;
                        }

                        game_object_list.Add(object_info);
                        this.move_queue.TryDequeue(out var _);
                    }

                    // game_object_list에 있는 모든 아이템을 패킷으로 만들어 전송
                    if (game_object_list.Any())
                    {
                        Packet packet = PacketMaker.MakeMapUpdatePacket(game_object_list);
                        this.Send(packet);

                        await Task.Delay(10);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.StackTrace} || {e.Message}");
                    await this.OnRemoved();
                }
            }

            try
            {
                this.world_update_task.Wait();
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"{e.StackTrace} || {e.Message}");
            }
        }

        async Task RecvGameObjectInfo()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (this.player_info == null)
                    {
                        continue;
                    }

                    if (this.player_info_list_queue.TryDequeue(out List<PlayerInfo>? info_list))
                    {
                        Packet player_info_packet = PacketMaker.MakePlayerInfoPacket(info_list);
                        this.Send(player_info_packet);
                    }

                    await Task.Delay(500);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.StackTrace} || {e.Message}");
                    await this.OnRemoved();
                }
            }

            try
            {
                this.game_object_info_task.Wait();
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"{e.StackTrace} || {e.Message}");
            }
        }

        public void OnMessage(Const<byte[]> buffer)
        {
            byte[] clone = new byte[Config.BUFFER_SIZE];
            Array.Copy(buffer.Value, clone, buffer.Value.Length);

            Packet packet = new(clone, this);
            Program.game_server.EnqueuePacket(packet);
        }

        async Task HandleMessage<T>(byte[] body, Func<T, Task> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            await handleMessage(msg);
        }

        public async Task ProcessUserOperation(Packet packet)
        {
            try
            {
                PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
                byte[] body = packet.PopBody();

                switch (protocol_id)
                {
                    case PROTOCOL.HEART_BEAT:
                        await HeartBeat();
                        break;

                    case PROTOCOL.C_TO_S_LOGIN:
                        await HandleMessage<C_TO_S_LOGIN>(body, Login);
                        break;

                    case PROTOCOL.C_TO_S_CHAT_MSG:
                        // await HandleMessage<C_TO_S_CHAT_MSG>(body, SendChat);
                        break;

                    case PROTOCOL.C_TO_S_MOVE:
                        await HandleMessage<C_TO_S_MOVE>(body, Move);
                        break;

                    case PROTOCOL.C_TO_S_PLAYER_INFO:
                        await HandleMessage<C_TO_S_PLAYER_INFO>(body, GetPlayerInfo);
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.Message}, {e.StackTrace}");
            }
        }

        async Task HeartBeat()
        {
            this.token.is_alive = true;
            await Program.game_server.HeartBeat(this);
        }

        async Task Login(C_TO_S_LOGIN request)
        {
            var player_info = await Program.game_server.LoginUserAsync(this);
            lock (this.player_info_lock)
            {
                this.player_info = player_info;
            }
        }

        async Task Move(C_TO_S_MOVE request)
        {
            await Program.game_server.MovePlayer(this, request.direction);
        }

        async Task GetPlayerInfo(C_TO_S_PLAYER_INFO request)
        {
            await Program.game_server.GetPlayerInfo(this, request.player_id_list);
        }

        public void Send(Packet msg)
        {
            this.token.Send(msg);
            Packet.Destroy(msg);
        }

        public async Task OnRemoved()
        {
            this.cts.Cancel();

            // await this.subscriber.UnsubscribeAllAsync();
            await Program.game_server.LeavePlayer(this);

            Console.WriteLine("The client disconnected.");
        }
    }
}
