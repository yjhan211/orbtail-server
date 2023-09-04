#pragma warning disable IDE1006

namespace game_server
{
    using network;

    public class Player : GameObject
    {
        public GameUser owner { get; private set; }
        public string name { get; private set; }

        object recv_world_info_lock;
        Task processing_task;
        public CancellationTokenSource cts;

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
                    // lock (recv_world_info_lock)
                    {
                        List<GameObject> game_object_list =
                            Program.game_server.GetBoundMapObjectList(this.target_cell);

                        for (int i = 0; i < game_object_list.Count; i += Config.BROADCAST_UNIT)
                        {
                            List<GameObjectMsg> chunk = game_object_list
                                .Skip(i)
                                .Take(Config.BROADCAST_UNIT)
                                .Select((game_object) => game_object.ParseToMsg())
                                .ToList();

                            Packet packet = GameServer.MakeMapInfoMsg(chunk);
                            this.Send(packet);

                            if (this.object_id == 1)
                            {
                                Console.WriteLine("send");
                            }

                            await Task.Delay(100);
                        }
                    }
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
