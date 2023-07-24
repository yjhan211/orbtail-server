#pragma warning disable IDE1006

namespace game_server
{
    using network;
    using MessagePack;

    public class GameUser : IPeer
    {
        public UserToken token { get; private set; }
        public long player_id { get; private set; }

        public GameUser(UserToken token)
        {
            this.token = token;

            this.token.is_alive = true;
            this.token.is_released = false;

            this.token.SetPeer(this);
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
            this.player_id = Program.game_server.LoginUser(this);
        }

        void SendChat(C_TO_S_CHAT_MSG request)
        {
            Program.game_server.SendChat(this.player_id, request.chat_message);
        }

        void Move(C_TO_S_MOVE request)
        {
            Program.game_server.Move(this.player_id, request.direction);
        }

        public void Send(Packet msg)
        {
            this.token.Send(msg);
        }

        public void OnRemoved()
        {
            Console.WriteLine("The client disconnected.");
            Program.game_server.LeaveUser(this.player_id);
        }
    }
}
