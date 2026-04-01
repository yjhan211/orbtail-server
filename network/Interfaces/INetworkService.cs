using System.Net;
using System.Net.Sockets;
using network.core;

namespace network.interfaces;

public interface INetworkService
{
    Action<UserToken>? SessionCreatedCallback { get; set; }

    void Listen(IPAddress address, short port);
    void OnConnectCompleted(Socket socket, UserToken userToken);
    void CloseClientSocket(UserToken? userToken);
}
