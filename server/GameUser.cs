#pragma warning disable CS8618
#pragma warning disable IDE1006

namespace game_server
{
    using network;

    class GameUser : IPeer
    {
        readonly int user_uid; // TODO 임시
        public string name { get; private set; }
        readonly UserToken token;

        public GameUser(int user_uid, UserToken token)
        {
            this.user_uid = user_uid;
            this.token = token;
            this.token.SetPeer(this);
        }

        void IPeer.OnMessage(Const<byte[]> buffer)
        {
            Packet packet = new(buffer.Value, this);
            PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
            byte[] body = packet.PopBody();
            switch (protocol_id)
            {
                case PROTOCOL.HEART_BEAT:
                    token.is_alive = true;
                    break;

                case PROTOCOL.C_TO_S_LOGIN:
                    var request = MessagePack.MessagePackSerializer.Deserialize<C_TO_S_LOGIN>(body);
                    var result = this.Login(request);
                    Packet response = MakePacket(
                        (int)PROTOCOL.S_TO_C_LOGIN,
                        MessagePack.MessagePackSerializer.Serialize(result)
                    );
                    Send(response);
                    break;
            }
        }

        public static Packet MakePacket(int protocol_id, byte[] body)
        {
            Packet result_packet = Packet.Create(protocol_id);
            result_packet.SetBody(body);

            return result_packet;
        }

        public void Send(Packet msg)
        {
            this.token.Send(msg);
        }

        public void OnRemoved()
        {
            Console.WriteLine("The client disconnected.");

            this.token.socket.Disconnect(false);
            Program.RemoveUser(this);
        }

        public void ProcessUserOperation(Packet msg)
        {
            throw new NotImplementedException();
        }

        S_TO_C_LOGIN Login(C_TO_S_LOGIN request)
        {
            // TODO request.account_token 검증 후 유저 정보 로드
            S_TO_C_LOGIN result = new() { user_uid = this.user_uid, name = $"플레이어{user_uid}" };

            return result;
        }
    }
}
