using network.common.data.models;

namespace network.interfaces;

public interface IUpdateHandler<in T> where T : IMessagePackObject
{
    IPacket MakePacket(T info);
}