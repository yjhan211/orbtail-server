using network.utils;
using network.common;

namespace network.packets
{
    public class PacketBufferPool
    {
        private static readonly ObjectPool<Packet> _pool = new(() => new Packet(), Config.MAX_CONNECTION);

        public static Packet Pop()
        {
            return _pool.Pop();
        }
        public static void Push(Packet packet)
        {
            _pool.Push(packet);
        }
    }
}
