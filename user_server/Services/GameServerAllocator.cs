using Microsoft.Extensions.Logging;
using network.contracts.routing;
using network.interfaces;

namespace user_server.services;

/// <summary>
///     매치 하나가 배정된 Game Server. 클라이언트에는 주소가, ticket에는 노드 ID가 실린다.
/// </summary>
internal sealed record GameServerAllocation(string PublicHost, int PublicPort, string NodeId);

internal interface IGameServerAllocator
{
    /// <summary>배정 가능한 노드가 없으면 null — 호출자는 큐를 건드리지 않고 다음 pass를 기다린다.</summary>
    public Task<GameServerAllocation?> TryAllocateAsync();
}

/// <summary>
///     레지스트리 descriptor만으로 노드를 고르는 순수 정책. 신선하고(accepting, 하트비트가 <paramref name="maxAge" /> 안)
///     부하 비율(활성 + 아직 하트비트에 반영되지 않은 배정) / 용량이 가장 낮은 노드, 같으면 nodeId 순.
/// </summary>
internal static class GameServerSelectionPolicy
{
    public static GameServerNodeDescriptor? Select(
        IReadOnlyList<GameServerNodeDescriptor> nodes,
        long nowUnixMs,
        TimeSpan maxAge,
        Func<GameServerNodeDescriptor, int> pendingAssignments)
    {
        GameServerNodeDescriptor? best = null;
        double bestLoad = double.PositiveInfinity;
        long staleBefore = nowUnixMs - (long)maxAge.TotalMilliseconds;
        foreach (GameServerNodeDescriptor node in nodes)
        {
            if (!node.Accepting || node.HeartbeatUnixMs < staleBefore)
                continue;

            int occupied = node.ActiveMatches + Math.Max(0, pendingAssignments(node));
            if (occupied >= node.MaxConcurrentMatches)
                continue;

            double load = (double)occupied / node.MaxConcurrentMatches;
            if (best != null &&
                (load > bestLoad ||
                 (load == bestLoad && string.CompareOrdinal(node.NodeId, best.NodeId) >= 0)))
                continue;

            best = node;
            bestLoad = load;
        }

        return best;
    }
}

/// <summary>
///     매칭 pass가 매치마다 부르는 배정자. 정책은 <see cref="GameServerSelectionPolicy" />에 있고, 여기서는
///     "이 프로세스가 방금 배정했지만 노드의 다음 하트비트에 아직 안 보이는" 매치 수를 노드별로 기억해
///     2초 창 안의 연속 배정이 한 노드로 쏠리지 않게 한다. 매칭 pass 한 스레드에서만 부른다.
/// </summary>
internal sealed class GameServerAllocator(IGameServerRegistry registry, ILogger logger) : IGameServerAllocator
{
    /// <summary>하트비트(2초)를 몇 번 놓치면 죽은 노드로 볼지 — 5회.</summary>
    public static readonly TimeSpan MaximumNodeAge = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan NoNodeWarningInterval = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, List<long>> _pendingAssignmentsByNode = new(StringComparer.Ordinal);
    private long _lastNoNodeWarningUnixMs;

    public async Task<GameServerAllocation?> TryAllocateAsync()
    {
        IReadOnlyList<GameServerNodeDescriptor> nodes = await registry.DiscoverAsync();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        PruneStaleAssignments(now);

        GameServerNodeDescriptor? node = GameServerSelectionPolicy.Select(
            nodes,
            now,
            MaximumNodeAge,
            candidate => CountPendingAssignments(candidate.NodeId, candidate.HeartbeatUnixMs));
        if (node == null)
        {
            WarnNoNodeAvailable(nodes, now);
            return null;
        }

        RecordAssignment(node.NodeId, now);
        return new GameServerAllocation(node.PublicHost, node.PublicPort, node.NodeId);
    }

    private int CountPendingAssignments(string nodeId, long heartbeatUnixMs)
    {
        if (!_pendingAssignmentsByNode.TryGetValue(nodeId, out List<long>? assignments))
            return 0;
        // 하트비트 이후의 배정만 — 그 전 것은 activeMatches에 이미 들어 있거나 입장에 실패해 사라졌다.
        // 같은 밀리초는 반영 여부를 알 수 없으니 미반영으로 센다 (과다 계산이 쏠림보다 싸다).
        return assignments.Count(assignedAt => assignedAt >= heartbeatUnixMs);
    }

    private void RecordAssignment(string nodeId, long now)
    {
        if (!_pendingAssignmentsByNode.TryGetValue(nodeId, out List<long>? assignments))
        {
            assignments = new List<long>();
            _pendingAssignmentsByNode[nodeId] = assignments;
        }

        assignments.Add(now);
    }

    private void PruneStaleAssignments(long now)
    {
        long keepAfter = now - (long)MaximumNodeAge.TotalMilliseconds;
        foreach (List<long> assignments in _pendingAssignmentsByNode.Values)
            assignments.RemoveAll(assignedAt => assignedAt < keepAfter);
    }

    private void WarnNoNodeAvailable(IReadOnlyList<GameServerNodeDescriptor> nodes, long now)
    {
        if (now - _lastNoNodeWarningUnixMs < NoNodeWarningInterval.TotalMilliseconds)
            return;

        _lastNoNodeWarningUnixMs = now;
        logger.LogWarning(
            "No game server node can take a match; matching waits: Registered={Registered}, Accepting={Accepting}, Fresh={Fresh}",
            nodes.Count,
            nodes.Count(node => node.Accepting),
            nodes.Count(node => now - node.HeartbeatUnixMs <= MaximumNodeAge.TotalMilliseconds));
    }
}
