using System.Net;
using System.Net.Sockets;
using network.core;

namespace network.interfaces;

public interface INetworkService
{
    public Action<UserToken>? SessionCreatedCallback { get; set; }

    public void Listen(IPAddress address, short port);
    public void CloseClientSocket(UserToken? userToken);
    public Task StopAsync(CancellationToken cancellationToken = default);
}
