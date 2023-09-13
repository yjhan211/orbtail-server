#pragma warning disable IDE1006

namespace game_server
{
    using network;
    using MessagePack;
    using System.Collections.Concurrent;

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

        public long GetPlayerId()
        {
            return this.player_id;
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
            try
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

                    case PROTOCOL.C_TO_S_PLAYER_INFO:
                        HandleMessage<C_TO_S_PLAYER_INFO>(body, GetPlayerInfo);
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.Message}, {e.StackTrace}");
            }
        }

        void HeartBeat()
        {
            this.token.is_alive = true;
            Program.game_server.HeartBeat(this.player_id);
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
            Program.game_server.MovePlayer(this.player_id, request.direction);
        }

        void GetPlayerInfo(C_TO_S_PLAYER_INFO request)
        {
            Program.game_server.GetPlayerInfo(this.player_id, request.player_id);
        }

        public void Send(Packet msg)
        {
            this.token.Send(msg);
        }

        public void OnRemoved()
        {
            Console.WriteLine("The client disconnected.");
            Program.game_server.LeavePlayer(this.player_id);
        }
    }
}
