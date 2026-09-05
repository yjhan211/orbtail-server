using Microsoft.Extensions.Configuration;

namespace user_server;

/// <summary>
///     이 프로세스의 노드 ID. 리더 lease 값이자 세션 알림의 origin.
///     <c>USER_SERVER_ID</c>가 없으면 컨테이너 호스트명 — compose·k8s 모두 유일하다.
/// </summary>
public sealed class UserServerNodeIdentity
{
    public UserServerNodeIdentity(IConfiguration configuration)
    {
        string? configured = configuration["USER_SERVER_ID"];
        NodeId = string.IsNullOrWhiteSpace(configured) ? Environment.MachineName : configured.Trim();
    }

    public string NodeId { get; }
}
