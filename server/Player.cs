#pragma warning disable IDE1006

namespace game_server
{
    using network;

    public class Player : GameObject
    {
        public GameUser owner { get; private set; }
        public string name { get; private set; }
        public CellPosition target_cell { get; set; }
        public DateTime move_timestamp { get; set; }
        public bool is_flip { get; set; }

        object recv_world_info_lock;
        Task processing_task;
        CancellationTokenSource cts;

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

        public PlayerObj ConvertObj()
        {
            PlayerObj player_obj =
                new()
                {
                    player_id = this.object_id,
                    name = this.name,
                    current_cell = this.current_cell,
                    target_cell = this.target_cell,
                    move_timestamp = this.move_timestamp,
                    is_flip = this.is_flip,
                };

            return player_obj;
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
                        List<PlayerObj> player_list = new List<PlayerObj>();

                        List<GameObject> game_objects = Program.game_server.GetBoundGameObjectList(
                            this.target_cell
                        );

                        foreach (GameObject game_object in game_objects)
                        {
                            if (game_object is Player player_object)
                            {
                                player_list.Add(player_object.ConvertObj());
                            }
                        }

                        List<List<PlayerObj>> chunks = player_list
                            .Select((player, index) => new { player, index })
                            .GroupBy(pair => pair.index / Config.BROADCAST_UNIT)
                            .Select(group => group.Select(pair => pair.player).ToList())
                            .ToList();

                        foreach (var chunk in chunks)
                        {
                            Packet packet = GameServer.MakeMapInfoObject(chunk);
                            this.Send(packet);
                        }
                    }

                    await Task.Delay(500);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.StackTrace} || {e.Message}");
                }
            }
        }
    }
}
