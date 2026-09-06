using network.routing;

namespace user_server.matching.creation;

/// <summary>
///     새 매치를 받으며 하트비트가 유효하고 정원이 남은 GameServer 중 하나를 선택한다.
///     활성 매치 수와 최근 배정 수를 합산해 정원 대비 사용 비율이 가장 낮은 서버를 고른다.
///     비율이 같으면 노드 ID 순으로 선택한다. Redis 조회나 배정 기록은 하지 않는다.
/// </summary>
internal static class GameServerSelectionPolicy
{
    public static GameServerNodeDescriptor? Select(
        IReadOnlyList<GameServerNodeDescriptor> nodes,
        long nowUnixMs,
        TimeSpan maxAge,
        Func<GameServerNodeDescriptor, int> pendingAssignments)
    {
        GameServerNodeDescriptor? selectedNode = null;
        long selectedOccupiedMatchCount = 0;
        long oldestAllowedHeartbeat = nowUnixMs - (long)maxAge.TotalMilliseconds;

        foreach (var node in nodes)
        {
            if (!node.Accepting)
            {
                continue;
            }

            if (node.HeartbeatUnixMs < oldestAllowedHeartbeat)
            {
                continue;
            }

            int pendingMatchCount = Math.Max(0, pendingAssignments(node));
            long occupiedMatchCount = (long)node.ActiveMatches + pendingMatchCount;
            if (occupiedMatchCount >= node.MaxConcurrentMatches)
            {
                continue;
            }

            if (selectedNode != null)
            {
                long candidateWeight = occupiedMatchCount * selectedNode.MaxConcurrentMatches;
                long selectedWeight = selectedOccupiedMatchCount * node.MaxConcurrentMatches;
                if (candidateWeight > selectedWeight)
                {
                    continue;
                }
                if (candidateWeight == selectedWeight && string.CompareOrdinal(node.NodeId, selectedNode.NodeId) >= 0)
                {
                    continue;
                }
            }

            selectedNode = node;
            selectedOccupiedMatchCount = occupiedMatchCount;
        }

        return selectedNode;
    }
}
