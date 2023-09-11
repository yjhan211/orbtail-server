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
        public object user_lock;
        Task processing_task;

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

            this.cts = new();
            this.bound_player_id_list = new();

            this.user_lock = new();

            this.processing_task = Task.Run(RecvWorldInfo, cts.Token);
        }

        public void Send(Packet msg)
        {
            this.owner.Send(msg);
            Packet.Destroy(msg);
        }

        public void SetFlip(DirectionType direction)
        {
            switch (direction)
            {
                case DirectionType.TOP_LEFT:
                case DirectionType.BOTTOM_LEFT:
                    this.is_flip = true;
                    break;

                default:
                    this.is_flip = false;
                    break;
            }
        }

        public double GetMoveElapsedTime()
        {
            TimeSpan elapsedTime = DateTime.UtcNow - this.move_timestamp;
            return elapsedTime.TotalSeconds;
        }

        async Task RecvWorldInfo()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    (List<GameObject> game_object_list, List<long> out_bound_id_list) =
                        Program.game_server.map_controller.GetBoundMapObjectList(
                            this.target_cell,
                            ref bound_player_id_list
                        );

                    if (game_object_list.Count <= 0)
                    {
                        Packet packet = GameServer.MakeMapInfoMsg(new(), out_bound_id_list, true);
                        this.Send(packet);
                    }

                    for (int i = 0; i < game_object_list.Count; i += Config.BROADCAST_UNIT)
                    {
                        List<GameObjectMsg> chunk = game_object_list
                            .Skip(i)
                            .Take(Config.BROADCAST_UNIT)
                            .Select((game_object) => game_object.ParseToMsg())
                            .ToList();

                        Packet packet = GameServer.MakeMapInfoMsg(
                            chunk,
                            out_bound_id_list,
                            game_object_list.Count <= (i + Config.BROADCAST_UNIT)
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
                processing_task.Wait();
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"{e.StackTrace} || {e.Message}");
            }
        }
    }
}
