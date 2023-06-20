using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace network
{
    public interface IPeer
    {
        void onMessage(Const<byte[]> buffer);

        void onRemoved();

        void send(Packet msg);

        void disconnect();

        void processUserOperation(Packet msg);
    }
}
