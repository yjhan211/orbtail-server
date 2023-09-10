#pragma warning disable IDE1006

namespace game_server
{
    using System.Numerics;
    using network;

    public class Player : GameObject
    {
        public GameUser owner { get; private set; }
        public string name { get; private set; }

        object recv_world_info_lock;
        Task processing_task;
        public CancellationTokenSource cts;

        public List<long> bound_player_id_list;

        public Player(GameUser user, long player_id, string name)
        {
            this.object_type = ObjectType.PLAYER;

            this.owner = user;
            this.object_id = player_id;
            this.name = name;

            this.current_cell = new CellPosition(0, 0);
            this.target_cell = new CellPosition(0, 0);
            this.move_timestamp = DateTime.MinValue;

            this.is_flip = false;

            this.recv_world_info_lock = new();
            this.cts = new();
            this.bound_player_id_list = new();
            this.processing_task = Task.Run(RecvWorldInfo, cts.Token);
        }

        public void Send(Packet msg)
        {
            this.owner.Send(msg);
            Packet.Destroy(msg);
        }

        async Task RecvWorldInfo()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    lock (recv_world_info_lock)
                    {
                        (List<GameObject> game_object_list, List<long> out_bound_id_list) =
                            Program.game_server.GetBoundMapObjectList(
                                this.target_cell,
                                ref bound_player_id_list
                            );

                        for (int i = 0; i < out_bound_id_list.Count; i++)
                        {
                            this.bound_player_id_list.Remove(out_bound_id_list[i]);
                        }

                        if (this.object_id > 100)
                        {
                            Console.WriteLine($"bound: {bound_player_id_list.Count}");
                            Console.WriteLine($"target: {game_object_list.Count}");
                        }

                        for (int i = 0; i < game_object_list.Count; i += Config.BROADCAST_UNIT)
                        {
                            List<GameObjectMsg> chunk = game_object_list
                                .Skip(i)
                                .Take(Config.BROADCAST_UNIT)
                                .Select((game_object) => game_object.ParseToMsg())
                                .ToList();

                            Packet packet = GameServer.MakeMapInfoMsg(chunk);
                            this.Send(packet);

                            // if (this.object_id == 1)
                            // {
                            //     Console.WriteLine("send");
                            // }
                        }

                        // Packet outbound_packet = GameServer.MakeOutBoundInfoMsg(out_bound_id_list);
                        // this.Send(outbound_packet);
                    }
                    await Task.Delay(300);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.StackTrace} || {e.Message}");
                    this.owner.OnRemoved();
                }
            }

            try
            {
                processing_task.Wait();
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"{e.StackTrace} || {e.Message}");
            }
        }
    }
}
