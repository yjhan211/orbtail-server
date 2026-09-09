using Microsoft.Extensions.Configuration;

namespace user_server;

/// <summary>
///     이 User Server의 노드 설정. 노드 ID는 리더 lease와 세션 알림의 발신자 식별에 사용한다.
///     <c>USER_SERVER_ID</c>가 없으면 호스트명을 사용한다. 여러 인스턴스를 띄울 때는 ID가 겹치지 않도록 설정한다.
/// </summary>
public sealed class UserServerNodeOptions
{
    public UserServerNodeOptions(IConfiguration configuration)
    {
        string? configured = configuration["USER_SERVER_ID"];
        NodeId = string.IsNullOrWhiteSpace(configured) ? Environment.MachineName : configured.Trim();
    }

    public string NodeId { get; }
}
