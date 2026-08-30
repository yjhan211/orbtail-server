using network.utils;

namespace network.interfaces;

public interface IPeer
{
    public Task OnMessageFromClient(Const<byte[]> buffer);
    public void OnDisconnect();
    public void OnRemoved();
    public void Send(IPacket msg);
}
