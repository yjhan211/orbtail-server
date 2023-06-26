using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using network;

namespace game_server
{
    public class GameServer
    {
        object player_list_lock = new();
        List<Player> player_list = new();
        bool test;
        object operation_lock;
        Queue<Packet> user_operations;

        // 로직은 하나의 스레드로만 처리한다.
        Thread logic_thread;
        AutoResetEvent loop_event;

        public GameServer()
        {
            this.operation_lock = new object();
            this.loop_event = new AutoResetEvent(false);
            this.user_operations = new Queue<Packet>();

            this.logic_thread = new Thread(GameLoop);
            this.logic_thread.Start();
        }

        void GameLoop()
        {
            while (true)
            {
                Packet packet = null;
                lock (this.operation_lock)
                {
                    if (this.user_operations.Count > 0)
                    {
                        packet = this.user_operations.Dequeue();
                    }
                }

                if (packet != null)
                {
                    // 패킷 처리.
                    ProcessReceive(packet);
                }

                // 더이상 처리할 패킷이 없으면 스레드 대기.
                if (this.user_operations.Count <= 0)
                {
                    this.loop_event.WaitOne();
                }
            }
        }

        public void JoinUser(Player player)
        {
            lock (this.player_list_lock)
            {
                this.player_list.Add(player);
            }

            Packet response = GameUser.MakePacket(
                (int)PROTOCOL.S_TO_C_LOGIN_ALL,
                MessagePack.MessagePackSerializer.Serialize(
                    new S_TO_C_LOGIN_ALL()
                    {
                        user_uid = player.owner.user_uid,
                        name = player.owner.name,
                    }
                )
            );

            Console.WriteLine($"user join success. count:{this.player_list.Count}");

            this.broadcast(response);
        }

        public void EnqueuePacket(Packet packet)
        {
            lock (this.operation_lock)
            {
                this.user_operations.Enqueue(packet);
                this.loop_event.Set();
            }
        }

        void broadcast(Packet msg)
        {
            this.player_list.ForEach(player => player.Send(msg, true));
            Packet.Destroy(msg);
        }

        void ProcessReceive(Packet msg)
        {
            //todo:
            // user msg filter 체크.
            msg.owner.ProcessUserOperation(msg);
        }

        public void UserDisconnected(GameUser user)
        {
            // if (this.matching_waiting_users.Contains(user))
            // {
            //     this.matching_waiting_users.Remove(user);
            // }
        }
    }
}
