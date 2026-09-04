using network.common;
using network.utils;

namespace network.packets;

/// <summary>
///     Packet 객체를 보관했다가 재사용하는 공용 풀.
///     Pop으로 객체를 빌리고, 사용이 끝나면 Packet.Dispose를 통해 반납한다.
///     초기 개수는 MAX_CONNECTION이며, 풀이 비어 있으면 새 객체를 생성한다.
///     반납한 객체는 다시 사용하거나 중복 반납하면 안 된다.
/// </summary>
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
