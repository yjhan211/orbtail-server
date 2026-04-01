using network.utils;

namespace network.interfaces;

public interface IPeer
{
    Task OnMessageFromClient(Const<byte[]> buffer);
    void OnRemoved();
    void Send(IPacket msg);
}
