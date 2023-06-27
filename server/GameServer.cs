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

        public int latest_user_uid = 0;

        Player MakePlayer(GameUser user)
        {
            Interlocked.Increment(ref latest_user_uid);
            Player player = new(user, latest_user_uid, name: $"플레이어{latest_user_uid}");

            return player;
        }

        public (Player, List<PlayerObj>) JoinUser(GameUser user)
        {
            Player player;
            List<PlayerObj> player_list;
            lock (this.player_list_lock)
            {
                player = MakePlayer(user);
                this.player_list.Add(player);

                player_list = this.player_list
                    .Select(
                        player_info => new PlayerObj(player_info.GetUserUid(), player_info.name)
                    )
                    .ToList();
            }

            return (player, player_list);
        }

        public void EnqueuePacket(Packet packet)
        {
            lock (this.operation_lock)
            {
                this.user_operations.Enqueue(packet);
                this.loop_event.Set();
            }
        }

        public void Broadcast(Packet msg)
        {
            Packet clone = new Packet();
            msg.CopyTo(clone);
            lock (this.player_list_lock)
            {
                this.player_list.ForEach(player => player.Send(msg, true));
            }

            // this.player_list.ForEach(player => player.Send(msg, true));
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
