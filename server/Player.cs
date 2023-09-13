#pragma warning disable IDE1006

namespace game_server
{
    using System.Numerics;
    using network;

    public class Player : GameObject
    {
        public GameUser owner { get; private set; }
        public string name { get; private set; }
        public CancellationTokenSource cts;
        public List<long> bound_player_id_list;
        public Queue<List<PlayerMsg>> player_msg_queue;
        public object user_lock;
        Task world_info_task;
        Task game_object_info_task;

        public Player(GameUser user, long player_id, string name, Cell init_cell)
        {
            this.object_type = ObjectType.PLAYER;

            this.owner = user;
            this.object_id = player_id;
            this.name = name;

            this.current_cell = init_cell;
            this.target_cell = init_cell;

            this.cts = new();
            this.bound_player_id_list = new();
            this.player_msg_queue = new();

            this.user_lock = new();

            this.world_info_task = Task.Run(RecvWorldInfo, cts.Token);
            this.game_object_info_task = Task.Run(RecvGameObjectInfo, cts.Token);
        }

        public void Send(Packet msg)
        {
            this.owner.Send(msg);
            Packet.Destroy(msg);
        }

        public PlayerMsg ParsePlayerMsg()
        {
            PlayerMsg msg = new PlayerMsg { player_id = this.object_id, name = this.name };

            return msg;
        }

        async Task RecvWorldInfo()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    List<GameObject> game_object_list;
                    List<long> out_bound_id_list;

                    lock (this.user_lock)
                    {
                        (game_object_list, out_bound_id_list) =
                            Program.game_server.map_controller.GetBoundMapObjectList(
                                this.target_cell,
                                ref bound_player_id_list
                            );
                    }

                    for (int i = 0; i < game_object_list.Count; i += Config.BROADCAST_UNIT)
                    {
                        List<GameObjectMsg> chunk = game_object_list
                            .Skip(i)
                            .Take(Config.BROADCAST_UNIT)
                            .Select((game_object) => game_object.ParseObjectMsg())
                            .ToList();

                        Packet packet = PacketMaker.MakeMapInfoPacket(
                            chunk,
                            out_bound_id_list,
                            game_object_list.Count <= (i + Config.BROADCAST_UNIT)
                        );
                        this.Send(packet);
                    }

                    if (game_object_list.Count == 0 && out_bound_id_list.Count != 0)
                    {
                        Packet packet = PacketMaker.MakeMapInfoPacket(
                            new(),
                            out_bound_id_list,
                            true
                        );
                        this.Send(packet);
                    }

                    await Task.Delay(200);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.StackTrace} || {e.Message}");
                    this.owner.OnRemoved();
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
                    lock (this.user_lock)
                    {
                        if (this.player_msg_queue.TryDequeue(out List<PlayerMsg>? player_msg_list))
                        {
                            if (player_msg_list == null)
                            {
                                continue;
                            }

                            player_msg_list = player_msg_list.FindAll(
                                (player_msg) =>
                                    this.bound_player_id_list.Contains(player_msg.player_id)
                            );

                            Packet player_info_packet = PacketMaker.MakePlayerInfoPacket(
                                player_msg_list
                            );

                            this.Send(player_info_packet);
                        }
                    }

                    await Task.Delay(500);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.StackTrace} || {e.Message}");
                    this.owner.OnRemoved();
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
    }
}
