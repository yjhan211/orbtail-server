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

        Task OnRemoved();

        void Send(Packet msg);

        Task ProcessUserOperation(Packet msg);
    }
}
