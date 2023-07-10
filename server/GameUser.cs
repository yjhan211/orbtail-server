#pragma warning disable CS8618
#pragma warning disable IDE1006

namespace game_server
{
    using network;
    using MessagePack;
    using UnityEngine;

    public class GameUser : IPeer
    {
        public UserToken token { get; private set; }
        public Player player { get; private set; }

        public GameUser(UserToken token)
        {
            this.token = token;

            this.token.is_alive = true;
            this.token.is_released = false;

            this.token.SetPeer(this);
        }

        public int GetPlayerUid()
        {
            return this.player.player_id;
        }

        public void OnMessage(Const<byte[]> buffer)
        {
            byte[] clone = new byte[Config.BUFFER_SIZE];
            Array.Copy(buffer.Value, clone, buffer.Value.Length);

            Packet packet = new(clone, this);
            Program.game_server.EnqueuePacket(packet);
        }

        static void HandleMessage<T>(byte[] body, Action<T> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            handleMessage(msg);
        }

        public void ProcessUserOperation(Packet packet)
        {
            PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
            byte[] body = packet.PopBody();

            switch (protocol_id)
            {
                case PROTOCOL.HEART_BEAT:
                    HeartBeat();
                    break;

                case PROTOCOL.C_TO_S_LOGIN:
                    HandleMessage<C_TO_S_LOGIN>(body, Login);
                    break;

                case PROTOCOL.C_TO_S_CHAT_MSG:
                    HandleMessage<C_TO_S_CHAT_MSG>(body, SendChat);
                    break;

                case PROTOCOL.C_TO_S_MOVE:
                    HandleMessage<C_TO_S_MOVE>(body, Move);
                    break;
            }
        }

        void HeartBeat()
        {
            this.token.is_alive = true;
        }

        void Login(C_TO_S_LOGIN request)
        {
            (this.player, List<PlayerObj> player_list) = Program.game_server.LoginUser(this);
            Packet packet = GameServer.MakeLoginPacket(this.player, player_list);

            Send(packet);
        }

        void SendChat(C_TO_S_CHAT_MSG request)
        {
            if (this.player == null)
            {
                return;
            }

            GameServer.SendChat(this.player, chat_message: request.chat_message);
        }

        void Move(C_TO_S_MOVE request)
        {
            if (this.player == null)
            {
                return;
            }

            // if (this.player.move_timestamp < 이동 소요시간)
            // {
            //     this.player.current_cell = this.player.target_cell;
            // }

            // Vector2 direction = request.direction;
            // // 여기서 direction에 위치한 target_cell을 구함
            // this.player.target_cell = // 머시기...
            // this.player.move_timestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
        }

        public void Send(Packet msg)
        {
            this.token.Send(msg);
        }

        public void OnRemoved()
        {
            Console.WriteLine("The client disconnected.");
            Program.game_server.LeaveUser(this.player);
        }
    }
}
