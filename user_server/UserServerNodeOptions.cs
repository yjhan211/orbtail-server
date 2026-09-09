using Microsoft.Extensions.Configuration;

namespace user_server;

/// <summary>
///     이 User Server의 노드 ID를 설정한다.
///     매칭 리더 등록과 서버 간 세션 알림에서 이 서버를 식별하는 데 사용한다.
///     USER_SERVER_ID가 없으면 호스트명을 사용하며, 인스턴스마다 고유해야 한다.
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
