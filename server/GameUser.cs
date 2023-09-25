#pragma warning disable IDE1006

namespace game_server
{
    using network;
    using MessagePack;

    public class GameUser : IPeer
    {
        public UserToken token { get; private set; }
        public long player_id { get; private set; }

        public CancellationTokenSource cts;
        public Queue<List<PlayerInfo>> player_info_list_queue;

        Task world_info_task;
        Task game_object_info_task;

        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.is_alive = true;
            this.token.is_released = false;
            this.token.SetPeer(this);

            this.cts = new();
            this.player_info_list_queue = new();

            this.world_info_task = Task.Run(RecvWorldInfo, cts.Token);
            this.game_object_info_task = Task.Run(RecvGameObjectInfo, cts.Token);
        }

        async Task RecvWorldInfo()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    PlayerInfo? player_info = await PlayerInfo.Load(this.player_id);
                    if (player_info == null)
                    {
                        continue;
                    }

                    (List<GameObjectInfo> game_object_list, List<string> out_bound_list) =
                        await Program.game_server.map_controller.GetBoundObjList(player_info);

                    for (int i = 0; i < game_object_list.Count; i += Config.BROADCAST_UNIT)
                    {
                        List<GameObjectInfo> chunk = game_object_list
                            .Skip(i)
                            .Take(Config.BROADCAST_UNIT)
                            .ToList();

                        Packet packet = PacketMaker.MakeMapInfoPacket(
                            chunk,
                            out_bound_list,
                            game_object_list.Count <= (i + Config.BROADCAST_UNIT)
                        );

                        this.Send(packet);
                    }

                    if (game_object_list.Count == 0 && out_bound_list.Count != 0)
                    {
                        Packet packet = PacketMaker.MakeMapInfoPacket(new(), out_bound_list, true);

                        this.Send(packet);
                    }

                    await Task.Delay(200);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.StackTrace} || {e.Message}");
                    await this.OnRemoved();
                }
            }

            try
            {
                this.world_info_task.Wait();
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
                    PlayerInfo? player_info = await PlayerInfo.Load(player_id);
                    if (player_info == null)
                    {
                        continue;
                    }

                    if (this.player_info_list_queue.TryDequeue(out List<PlayerInfo>? info_list))
                    {
                        var bound_hash = await Program.redis_client.BasicRetryAsync(
                            (db) =>
                                db.HashGetAllAsync(MapController.GetBoundObjectKey(this.player_id))
                        );

                        var bound_object_key_list = bound_hash
                            .Select(entry => entry.Name.ToString())
                            .ToList();

                        info_list = info_list.FindAll(
                            (info) =>
                                bound_object_key_list.Contains(
                                    MapController.GetGameObjectKey(info.object_info)
                                )
                        );

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
            await Program.game_server.HeartBeat(this.player_id);
        }

        async Task Login(C_TO_S_LOGIN request)
        {
            this.player_id = await Program.game_server.LoginUserAsync(this);
        }

        // async Task SendChat(C_TO_S_CHAT_MSG request)
        // {
        //     // await Program.game_server.SendChat(this.player_id, request.chat_message);
        // }

        async Task Move(C_TO_S_MOVE request)
        {
            await Program.game_server.MovePlayer(this.player_id, request.direction);
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
            Console.WriteLine("The client disconnected.");
            await Program.game_server.LeavePlayer(this);
        }
    }
}
