using System.Net;
using System.Net.Sockets;
using network.core;

namespace network.interfaces;

public interface INetworkService
{
    public Action<UserToken>? SessionCreatedCallback { get; set; }

    public void Listen(IPAddress address, short port);
    public void OnConnectCompleted(Socket socket, UserToken userToken);
    public void CloseClientSocket(UserToken? userToken);
}
