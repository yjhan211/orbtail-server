using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace network
{
    public interface IPeer
    {
        Task OnMessage(Const<byte[]> buffer);

        Task OnRemoved();

        void Send(Packet msg);

        // Task ProcessUserOperation(Packet msg);
    }
}
