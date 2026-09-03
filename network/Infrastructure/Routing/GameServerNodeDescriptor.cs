using MessagePack;

namespace network.infrastructure.routing;

/// <summary>
///     Game Server 한 대가 레지스트리에 광고하는 자기 소개. User Server 매칭이 이 값만 보고 매치를 배정한다.
///     하트비트마다 통째로 덮어쓰며, <see cref="HeartbeatUnixMs" />가 오래되면 죽은 노드로 취급한다.
/// </summary>
[MessagePackObject]
public sealed class GameServerNodeDescriptor
{
    /// <summary>노드 식별자 — 배포 단위에서 고정된다(예: StatefulSet 파드 이름). ticket이 이 값에 결합된다.</summary>
    [Key("nodeId")] public string NodeId { get; set; } = string.Empty;

    /// <summary>클라이언트가 접속할 공개 주소.</summary>
    [Key("publicHost")] public string PublicHost { get; set; } = string.Empty;

    [Key("publicPort")] public int PublicPort { get; set; }

    /// <summary>동시에 돌릴 수 있는 매치 수 상한. 부하 비율 계산의 분모.</summary>
    [Key("maxConcurrentMatches")] public int MaxConcurrentMatches { get; set; }

    /// <summary>하트비트 시점의 활성 매치 수.</summary>
    [Key("activeMatches")] public int ActiveMatches { get; set; }

    /// <summary>false면 종료 중이라 새 매치를 받지 않는다. 진행 중인 매치는 끝까지 돈다.</summary>
    [Key("accepting")] public bool Accepting { get; set; }

    [Key("heartbeatUnixMs")] public long HeartbeatUnixMs { get; set; }

    [Key("startedUnixMs")] public long StartedUnixMs { get; set; }

    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(NodeId) &&
               !string.IsNullOrWhiteSpace(PublicHost) &&
               PublicPort is > 0 and <= ushort.MaxValue &&
               MaxConcurrentMatches > 0 &&
               ActiveMatches >= 0 &&
               HeartbeatUnixMs > 0;
    }
}
