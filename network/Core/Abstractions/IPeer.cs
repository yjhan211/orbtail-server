using network.packets;
using network.utils;

namespace network.core.abstractions;

public interface IPeer
{
    public Task OnMessageFromClient(Const<byte[]> buffer);
    public void OnDisconnect();
    public void OnRemoved();
    public void Send(Packet msg);
}
