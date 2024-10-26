using network.common;

namespace network.interfaces
{
    public interface IUpdateHandler<T> where T : IMessagePackObject
    {
        IPacket MakePacket(T info);
    }

}