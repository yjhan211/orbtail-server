using network.common;
using network.utils;

namespace network.packets;

public static class PacketBufferPool
{
    private static readonly ObjectPool<Packet> Pool = new(() => new Packet(), Config.MAX_CONNECTION);

    public static Packet Pop()
    {
        return Pool.Pop();
    }

    public static void Push(Packet packet)
    {
        Pool.Push(packet);
    }
}