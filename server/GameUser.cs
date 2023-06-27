#pragma warning disable CS8618
#pragma warning disable IDE1006

namespace game_server
{
    using network;

    public class GameUser : IPeer
    {
        readonly UserToken token;
        Player player;

        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.SetPeer(this);
        }

        public int GetUserUid()
        {
            return this.player?.GetUserUid() ?? 0;
        }

        void IPeer.OnMessage(Const<byte[]> buffer)
        {
            byte[] clone = new byte[1024];
            Array.Copy(buffer.Value, clone, buffer.Value.Length);

            Packet packet = new(clone, this);
            Program.game_server.EnqueuePacket(packet);
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

        public void ProcessUserOperation(Packet packet)
        {
            PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
            byte[] body = packet.PopBody();

            switch (protocol_id)
            {
                case PROTOCOL.HEART_BEAT:
                    token.is_alive = true;
                    break;

                case PROTOCOL.C_TO_S_LOGIN:
                    (this.player, List<PlayerObj> player_list) = Program.game_server.JoinUser(this);

                    S_TO_C_LOGIN response =
                        new()
                        {
                            user_uid = player.GetUserUid(),
                            name = player.name,
                            player_list = player_list,
                        };

                    Send(
                        MakePacket(
                            (int)PROTOCOL.S_TO_C_LOGIN,
                            MessagePack.MessagePackSerializer.Serialize(response)
                        )
                    );

                    Packet response2 = GameUser.MakePacket(
                        (int)PROTOCOL.S_TO_C_LOGIN_ALL,
                        MessagePack.MessagePackSerializer.Serialize(
                            new S_TO_C_LOGIN_ALL()
                            {
                                user_uid = player.user_uid,
                                name = player.name,
                            }
                        )
                    );
                    Program.game_server.Broadcast(response2);

                    break;
            }
        }
    }
}
