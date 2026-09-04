using MessagePack;

namespace network.routing;

/// <summary>
///     노드 식별자. 배포 환경에서 Game Server 인스턴스마다 고정되고 고유해야 한다.
///     예: Docker Compose의 gameServerId, Kubernetes StatefulSet의 Pod 이름.
///     GameHandoffContext의 GameServerNodeId.
/// </summary>
[MessagePackObject]
public sealed class GameServerNodeDescriptor
{
    [Key("nodeId")] public string NodeId { get; set; } = string.Empty;
    [Key("publicHost")] public string PublicHost { get; set; } = string.Empty;
    [Key("publicPort")] public int PublicPort { get; set; }
    [Key("maxConcurrentMatches")] public int MaxConcurrentMatches { get; set; }
    [Key("activeMatches")] public int ActiveMatches { get; set; }
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
