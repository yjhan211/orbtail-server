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
        readonly UserToken token;

        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.SetPeer(this);
        }

        public void OnMessage(Const<byte[]> buffer)
        {
            Packet msg = new(buffer.Value, this);
            PROTOCOL protocol = (PROTOCOL)msg.PopProtocolId();
            Console.WriteLine("------------------------------------------------------");
            Console.WriteLine("protocol id " + protocol);
            switch (protocol)
            {
                case PROTOCOL.BEGIN:
                    string text = msg.PopString();
                    Console.WriteLine(string.Format("text {0}", text));
                    token.is_alive = true;

                    Packet response = Packet.Create((int)PROTOCOL.BEGIN);
                    response.Push(text);
                    Send(response);
                    break;
            }
        }

        public void ProcessUserOperation(Packet msg) { }

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
    }
}
