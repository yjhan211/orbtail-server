using network.common;
using network.interfaces;

namespace game_server.handlers;

public class UpdateHandler<T>(Func<T, IPacket> packetMaker) : IUpdateHandler<T>
    where T : IMessagePackObject
{
    public IPacket MakePacket(T info)
    {
        return packetMaker(info);
    }
}