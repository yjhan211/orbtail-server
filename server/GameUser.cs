#pragma warning disable IDE1006

namespace game_server
{
    using network;
    using MessagePack;
    using System.Collections.Concurrent;
    using StackExchange.Redis;

    public class GameUser : IPeer
    {
        public UserToken token { get; private set; }
        public SemaphoreSlim player_lock;
        public PlayerInfo? player_info { get; set; }
        public CancellationTokenSource cts;
        public ConcurrentQueue<GameObjectInfo> move_queue;
        public ConcurrentQueue<List<PlayerInfo>> player_info_list_queue;
        public string? move_channel;
        Task? world_info_task;
        Task? world_update_task;
        Task? game_object_info_task;

        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.is_alive = true;
            this.token.is_released = false;
            this.token.SetPeer(this);

            this.player_lock = new(1);
            this.cts = new();

            this.move_queue = new();
            this.player_info_list_queue = new();

            this.move_channel = default;
        }

        string GetPositionKey(int map_id, Cell cell)
        {
            return $"map_{map_id}|{cell.x},{cell.y}";
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

                    await this.player_lock.WaitAsync();
                    Cell current_cell = Cell.Clone(player_info.object_info.current_cell);
                    this.player_lock.Release();

                    List<string> bound_cell_list = bound_cell_list = MapController
                        .GetBoundCellList(current_cell)
                        .Select((cell) => GetPositionKey(player_info.object_info.map_id, cell))
                        .ToList();

                    RedisValue[] game_object_keys = await CacheHelper.ListRange(bound_cell_list);
                    for (int i = 0; i < game_object_keys.Length; i += Config.BROADCAST_UNIT)
                    {
                        RedisValue[] batch = game_object_keys
                            .Skip(i)
                            .Take(Config.BROADCAST_UNIT)
                            .ToArray();

                        var remain = game_object_keys.Length - i - Config.BROADCAST_UNIT;
                        var is_ended = remain <= 0;

                        Packet packet = PacketMaker.MakeMapInfoPacket(
                            batch.Select((item) => item.ToString()).ToList(),
                            is_ended
                        );

                        this.Send(packet);
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
                this.world_info_task!.Wait();
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

                    // move_queue에 데이터가 없을 때까지 기다리고 비동기적으로 처리
                    while (!this.move_queue.Any())
                    {
                        await Task.Delay(100); // 200ms 대기
                    }

                    List<GameObjectInfo> game_object_list = new();
                    // move_queue에 데이터가 있을 때 리스트에 추가
                    while (this.move_queue.TryDequeue(out var object_info))
                    {
                        if (game_object_list.Count >= Config.BROADCAST_UNIT)
                        {
                            break;
                        }

                        game_object_list.Add(object_info);
                    }

                    Packet packet = PacketMaker.MakeMapUpdatePacket(game_object_list);
                    this.Send(packet);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.StackTrace} || {e.Message}");
                    await this.OnRemoved();
                }
            }

            try
            {
                this.world_update_task!.Wait();
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
                this.game_object_info_task!.Wait();
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
            bool is_lock = false;

            try
            {
                PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
                byte[] body = packet.PopBody();

                if (protocol_id != PROTOCOL.HEART_BEAT)
                {
                    is_lock = true;
                    await this.player_lock.WaitAsync();
                }

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

                    case PROTOCOL.C_TO_S_OBJECT_INFO:
                        await HandleMessage<C_TO_S_OBJECT_INFO>(body, GetObjectInfo);
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.Message}, {e.StackTrace}");
            }
            finally
            {
                if (is_lock)
                {
                    this.player_lock.Release();
                }
            }
        }

        async Task HeartBeat()
        {
            this.token.is_alive = true;
            await Program.game_server.HeartBeat(this);
        }

        async Task Login(C_TO_S_LOGIN request)
        {
            this.player_info = await Program.game_server.LoginUserAsync(this);
            await Program.game_server.MovePlayer(this, DirectionType.NONE);

            this.world_info_task = Task.Run(RecvWorldInfo, cts.Token);
            this.world_update_task = Task.Run(RecvSubscribed, cts.Token);
            this.game_object_info_task = Task.Run(RecvGameObjectInfo, cts.Token);
        }

        async Task Move(C_TO_S_MOVE request)
        {
            await Program.game_server.MovePlayer(this, request.direction);
        }

        async Task GetPlayerInfo(C_TO_S_PLAYER_INFO request)
        {
            await Program.game_server.GetPlayerInfo(this, request.player_id_list);
        }

        async Task GetObjectInfo(C_TO_S_OBJECT_INFO request)
        {
            await Program.game_server.GetObjectInfo(this, request.object_key_list);
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
