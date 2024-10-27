using network.common;

namespace network.interfaces;

public interface IUpdateHandler<in T> where T : IMessagePackObject
{
    IPacket MakePacket(T info);
}