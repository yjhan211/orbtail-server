using System.Net;
using System.Net.Sockets;
using network.core;

namespace network.core.abstractions;

public interface INetworkService
{
    /// <summary>
    ///     초기화된 연결에 붙일 서버별 세션을 생성한다.
    ///     세션 생성을 거절했거나 실패를 이미 처리한 경우 null을 반환한다.
    /// </summary>
    public Func<UserToken, IPeer?>? SessionFactory { get; set; }
    public void Listen(IPAddress address, short port);
    public void CloseClientSocket(UserToken? userToken);
    public Task StopAsync(CancellationToken cancellationToken = default);
}
