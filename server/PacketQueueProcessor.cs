using System.Collections.Concurrent;

namespace game_server
{
    using System.Net.Sockets;
    using MessagePack;
    using network;

    public class PacketQueueProcessor
    {
        readonly ConcurrentQueue<(PROTOCOL, IMessagePackObject)> packet_queue;
        CancellationTokenSource cts;
        Task processing_task;
        bool is_processing;

        object loop_lock;

        GameUser owner;

        public PacketQueueProcessor(GameUser owner)
        {
            packet_queue = new();
            cts = new CancellationTokenSource();
            is_processing = false;

            // 각 큐의 처리를 위한 Task 실행
            processing_task = Task.Run(ProcessQueueAsync, cts.Token);

            this.owner = owner;

            this.loop_lock = new();
        }

        public void AddToQueue(PROTOCOL protocol, IMessagePackObject msg)
        {
            lock (packet_queue)
            {
                packet_queue.Enqueue((protocol, msg));
            }
        }

        public void StopProcessing()
        {
            cts.Cancel();
        }

        async Task ProcessQueueAsync()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    List<PlayerObj> player_spawn_list = new();
                    List<MoveObj> player_move_list = new();
                    List<long> player_destroy_list = new();

                    while (this.packet_queue.TryPeek(out var packet))
                    {
                        if (player_spawn_list.Count > Config.BROADCAST_UNIT)
                        {
                            break;
                        }
                        if (player_move_list.Count > Config.BROADCAST_UNIT * 2)
                        {
                            break;
                        }
                        if (player_destroy_list.Count > Config.BROADCAST_UNIT)
                        {
                            break;
                        }

                        switch (packet.Item1)
                        {
                            case PROTOCOL.S_TO_C_PLAYER_SPAWN_LIST:
                                player_spawn_list.Add((PlayerObj)packet.Item2);
                                break;

                            case PROTOCOL.S_TO_C_PLAYER_DESTROY_LIST:
                                PlayerObj destroy_player_obj = (PlayerObj)packet.Item2;
                                player_destroy_list.Add(destroy_player_obj.player_id);
                                break;

                            case PROTOCOL.S_TO_C_MOVE_LIST:
                                player_move_list.Add((MoveObj)packet.Item2);
                                break;

                            default:
                                break;
                        }
                        this.packet_queue.TryDequeue(out var _);
                    }

                    if (player_spawn_list.Count > 0)
                    {
                        Packet packet = GameServer.MakeSpawnPlayerListPacket(player_spawn_list);
                        owner.Send(packet);
                        Packet.Destroy(packet);
                    }

                    if (player_move_list.Count > 0)
                    {
                        Packet packet = GameServer.MakeMoveListPacket(player_move_list);
                        owner.Send(packet);
                        Console.WriteLine($"{player_move_list.Count}, packet:{packet.position}");
                        Packet.Destroy(packet);
                    }

                    if (player_destroy_list.Count > 0)
                    {
                        Packet packet = GameServer.MakeDestroyPlayerListPacket(player_destroy_list);
                        owner.Send(packet);
                        Packet.Destroy(packet);
                    }

                    await Task.Delay(20);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.StackTrace} || {e.Message}");
                }
            }
        }
    }
}
