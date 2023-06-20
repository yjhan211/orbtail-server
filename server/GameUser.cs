using network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace server
{
    using game_server;

    class GameUser : IPeer
    {
        UserToken token;

        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.setPeer(this);
        }

        public void onMessage(Const<byte[]> buffer)
        {
            Packet msg = new Packet(buffer.Value, this);
            PROTOCOL protocol = (PROTOCOL)msg.popProtocolId();
            Console.WriteLine("------------------------------------------------------");
            Console.WriteLine("protocol id " + protocol);
            switch (protocol)
            {
                case PROTOCOL.CHAT_MSG_REQ:
                    {
                        string text = msg.popString();
                        Console.WriteLine(string.Format("text {0}", text));

                        Packet response = Packet.create((short)PROTOCOL.CHAT_MSG_ACK);
                        response.push(text);
                        send(response);
                    }
                    break;
            }
        }

        public void send(Packet msg)
        {
            this.token.send(msg);
        }

        public void onRemoved()
        {
            Console.WriteLine("The client disconnected.");

            Program.removeUser(this);
        }

        public void processUserOperation(Packet msg)
        {
        }

        public void disconnect()
        {
            this.token.socket.Disconnect(false);
        }
    }
}
