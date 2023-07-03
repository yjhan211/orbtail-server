using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using network;

namespace game_server
{
    using MessagePack;

    public partial class GameServer
    {
        public static Packet MakeLoginPacket(Player player, List<PlayerObj> player_list)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_LOGIN);
            S_TO_C_LOGIN body = new() { player = player.ConvertObj(), player_list = player_list };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        static Packet MakeLoginAllPacket(Player player)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_LOGIN_ALL);
            S_TO_C_LOGIN_ALL body = new() { player = player.ConvertObj() };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        static Packet MakeLogoutAllPacket(Player player)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_LOGOUT_ALL);
            S_TO_C_LOGOUT_ALL body = new() { player = player.ConvertObj() };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        static Packet MakeChatPacket(Player player, string chat_message)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_CHAT_MSG_ALL);
            S_TO_C_CHAT_MSG_ALL body =
                new() { player_id = player.player_id, chat_message = chat_message };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }
    }
}
