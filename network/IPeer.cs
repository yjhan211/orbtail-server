using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace network
{
    public interface IPeer
    {
        void OnMessageFromClient(Const<byte[]> buffer);

        void OnRemoved();

        void SendToClient(Packet msg);

        // Task ProcessUserOperation(Packet msg);
    }
}
