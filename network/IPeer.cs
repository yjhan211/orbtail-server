using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace network
{
    public interface IPeer
    {
        void OnMessage(Const<byte[]> buffer);

        void OnRemoved();

        void Send(Packet msg);

        void Disconnect();

        void ProcessUserOperation(Packet msg);
    }
}
