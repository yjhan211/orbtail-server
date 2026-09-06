using Microsoft.Extensions.Logging;
using network.routing;

namespace user_server.matching.creation;

/// <summary>
///     레지스트리에서 GameServer 목록을 조회하고, 선택 정책에 따라 새 매치를 보낼 서버를 고른다.
///     하트비트에 아직 반영되지 않았을 수 있는 최근 배정을 기록해 연속 배정이 한 서버로 쏠리는 것을 줄인다.
///     선택 가능한 서버가 없으면 null을 반환한다.
///     매칭 작업에서 순차적으로 호출하며, GameServer의 자리를 직접 예약하지는 않는다.
/// </summary>
internal sealed class GameServerAllocator(IGameServerRegistry gameServerRegistry, ILogger<GameServerAllocator> logger) : IGameServerAllocator
{
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoAvailableServerWarningInterval = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, List<long>> _recentAssignmentTimesByNode = new(StringComparer.Ordinal);
    private long _lastNoAvailableServerWarningUnixMs;

    public async Task<GameServerAllocation?> TryAllocateAsync()
    {
        var nodes = await gameServerRegistry.DiscoverAsync();
        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RemoveOldAssignmentRecords(nowUnixMs);

        var node = GameServerSelectionPolicy.Select(
            nodes,
            nowUnixMs,
            HeartbeatTimeout,
            candidate => CountAssignmentsSinceHeartbeat(candidate.NodeId, candidate.HeartbeatUnixMs));

        if (node == null)
        {
            LogNoAvailableServer(nodes, nowUnixMs);
            return null;
        }

        RecordRecentAssignment(node.NodeId, nowUnixMs);
        return new GameServerAllocation(node.PublicHost, node.PublicPort, node.NodeId);
    }

    private int CountAssignmentsSinceHeartbeat(string nodeId, long heartbeatUnixMs)
    {
        return !_recentAssignmentTimesByNode.TryGetValue(nodeId, out var assignmentTimes) ? 0
            : assignmentTimes.Count(assignedAtUnixMs => assignedAtUnixMs >= heartbeatUnixMs);
    }

    private void RecordRecentAssignment(string nodeId, long nowUnixMs)
    {
        if (!_recentAssignmentTimesByNode.TryGetValue(nodeId, out List<long>? assignmentTimes))
        {
            assignmentTimes = new List<long>();
            _recentAssignmentTimesByNode[nodeId] = assignmentTimes;
        }

        assignmentTimes.Add(nowUnixMs);
    }

    private void RemoveOldAssignmentRecords(long nowUnixMs)
    {
        long oldestAllowedAssignmentUnixMs = nowUnixMs - (long)HeartbeatTimeout.TotalMilliseconds;
        foreach (var assignmentTimes in _recentAssignmentTimesByNode.Values)
        {
            assignmentTimes.RemoveAll(assignedAtUnixMs => assignedAtUnixMs < oldestAllowedAssignmentUnixMs);
        }
    }

    private void LogNoAvailableServer(IReadOnlyList<GameServerNodeDescriptor> nodes, long nowUnixMs)
    {
        if (nowUnixMs - _lastNoAvailableServerWarningUnixMs < NoAvailableServerWarningInterval.TotalMilliseconds)
            return;

        _lastNoAvailableServerWarningUnixMs = nowUnixMs;
        logger.LogWarning(
            "No game server node can take a match; matching waits: Registered={Registered}, Accepting={Accepting}, Fresh={Fresh}",
            nodes.Count,
            nodes.Count(node => node.Accepting),
            nodes.Count(node => nowUnixMs - node.HeartbeatUnixMs <= HeartbeatTimeout.TotalMilliseconds));
    }
}
