using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     #127: 봇 셀 단위 walking을 위한 pathfinding.
///     - 영역 그래프 BFS (어느 영역들을 거칠지)
///     - 영역별 셀 그리드 BFS (영역 안에서 어디로 갈지)
///     - 영역 경계 통과 시 GameAreaConnectionData의 문 양쪽 셀을 연속 보행
/// </summary>
public static class BotPathfinder
{
    private const int DoorClearanceStepCount = 2;

    /// <summary>경로의 한 단계. 셀 + 그 셀이 속한 영역 + 영역 전환 여부.</summary>
    public class Step
    {
        public Cell Cell { get; set; } = new(0, 0);
        public AreaType Area { get; set; }
        /// <summary>true면 문 건너편 영역의 첫 보행 웨이포인트.</summary>
        public bool IsAreaTransition { get; set; }
        /// <summary>영역 전환일 때 직전 영역 (LEAVE 패킷용).</summary>
        public AreaType FromAreaForTransition { get; set; }
    }

    /// <summary>
    ///     fromCell(fromArea) → toCell(toArea) 경로 계산.
    ///     반환된 List를 순서대로 따라가면 영역 변경 + 셀 이동이 자연스럽게 처리된다.
    ///     null 또는 비어있으면 경로 없음 (목표 도달 불가).
    /// </summary>
    public static List<Step>? FindPath(MapId mapId, AreaType fromArea, Cell fromCell,
        AreaType toArea, Cell toCell,
        Func<AreaType, bool>? isAreaBlocked = null)
    {
        // 1) 영역 시퀀스 BFS (인접 그래프) — 각 hop에서 사용된 connection도 함께 반환
        var areaSeq = BfsAreaGraph(mapId, fromArea, toArea, isAreaBlocked);
        if (areaSeq == null) return null;

        var path = new List<Step>();
        var currentCell = fromCell;

        // 2) 각 영역 사이 walk + 경계 통과
        for (int i = 0; i < areaSeq.Count - 1; i++)
        {
            var fromA = areaSeq[i].Area;
            var toA = areaSeq[i + 1].Area;

            // BFS는 임의의 conn을 선택하지만, 같은 (fromA → toA) 쌍에 west/east 두 conn이 있으면
            // 봇 현재 셀에서 fromA 측 도어가 더 가까운 쪽 우선 (계단 가까운 쪽 선택).
            var forwardConn = PickClosestConnection(mapId, fromA, toA, currentCell)
                              ?? areaSeq[i + 1].IncomingConn;

            // 현재 영역 측 도어 셀 = 반대 방향 conn(toA → fromA, 같은 StairSide)의 SpawnCell
            Cell? exitCell = null;
            if (forwardConn != null)
                exitCell = FindReverseSpawnCell(mapId, forwardConn);
            if (exitCell == null)
                exitCell = GameMapData.GetAreaSpawnCell(mapId, fromA); // 폴백

            // 현재 셀 → 출구 셀 (영역 안에서 walk)
            var cellPath = BfsCellsInArea(mapId, fromA, currentCell, exitCell);
            if (cellPath != null)
            {
                foreach (var c in cellPath.Skip(1)) // 시작 셀(현재 위치) 제외
                    path.Add(new Step { Cell = c, Area = fromA, IsAreaTransition = false });
            }

            // 영역 경계 통과: 선택된 connection의 SpawnCell은 대상 영역 문어귀의 인접 보행 셀이다.
            Cell entryCell = forwardConn?.SpawnCell ?? GameMapData.GetAreaSpawnCell(mapId, toA);
            path.Add(new Step
            {
                Cell = entryCell,
                Area = toA,
                IsAreaTransition = true,
                FromAreaForTransition = fromA
            });
            currentCell = entryCell;

            // Do not let a bot wait on the doorway spawn cell after its final area transition.
            // Existing paths with a separate in-room target continue through the normal final BFS.
            bool isFinalTransition = i == areaSeq.Count - 2;
            if (isFinalTransition && toCell.Equals(entryCell))
            {
                foreach (var clearanceCell in FindDoorClearanceCells(mapId, toA, exitCell, entryCell))
                {
                    path.Add(new Step
                    {
                        Cell = clearanceCell,
                        Area = toA,
                        IsAreaTransition = false
                    });
                    currentCell = clearanceCell;
                }

                // Prevent the final BFS from walking back to the requested doorway cell.
                toCell = currentCell;
            }
        }

        // 3) 마지막 영역 안에서 toCell까지 walk
        var finalPath = BfsCellsInArea(mapId, toArea, currentCell, toCell);
        if (finalPath != null)
        {
            foreach (var c in finalPath.Skip(1))
                path.Add(new Step { Cell = c, Area = toArea, IsAreaTransition = false });
        }

        return path;
    }

    private static IReadOnlyList<Cell> FindDoorClearanceCells(
        MapId mapId,
        AreaType targetArea,
        Cell exitCell,
        Cell entryCell)
    {
        int stepX = Math.Sign(entryCell.X - exitCell.X);
        int stepY = Math.Sign(entryCell.Y - exitCell.Y);
        if (stepX == 0 && stepY == 0) return Array.Empty<Cell>();

        var areaRegions = GameMapData.GetAreas(mapId)
            .Where(region => region.AreaType == targetArea)
            .ToList();
        if (areaRegions.Count == 0) return Array.Empty<Cell>();

        var result = new List<Cell>(DoorClearanceStepCount);
        var current = entryCell;
        for (int i = 0; i < DoorClearanceStepCount; i++)
        {
            var candidate = new Cell(current.X + stepX, current.Y + stepY);
            if (!IsWithinArea(areaRegions, candidate) || !GameMapData.IsMoveablePosition(mapId, candidate))
                break;

            result.Add(candidate);
            current = candidate;
        }

        return result;
    }

    /// <summary>BFS 영역 시퀀스 한 노드 — 그 영역으로 진입할 때 사용된 conn 정보를 함께 보관.</summary>
    private class AreaSeqNode
    {
        public AreaType Area { get; set; }
        /// <summary>이 영역으로 진입한 connection (시작 영역은 null). StairSide/SpawnCell 포함.</summary>
        public AreaConnectionInfo? IncomingConn { get; set; }
    }

    /// <summary>
    ///     영역 그래프 BFS. fromArea → toArea 최단 영역 시퀀스 + 사용된 connection.
    ///     같은 (from→to) 쌍에 west/east 두 conn이 있을 경우 SpawnCell이 (0,0) 아닌 첫 번째 사용.
    ///     isAreaBlocked가 true 반환하는 영역은 회피.
    /// </summary>
    private static List<AreaSeqNode>? BfsAreaGraph(MapId mapId, AreaType fromArea, AreaType toArea,
        Func<AreaType, bool>? isAreaBlocked)
    {
        if (fromArea == toArea)
            return new List<AreaSeqNode> { new() { Area = fromArea, IncomingConn = null } };

        var visited = new HashSet<AreaType> { fromArea };
        // parent[next] = (prev area, conn used to enter next)
        var parent = new Dictionary<AreaType, (AreaType prev, AreaConnectionInfo conn)>();
        var queue = new Queue<AreaType>();
        queue.Enqueue(fromArea);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var conn in GameAreaConnectionData.GetConnections(mapId, current))
            {
                var next = conn.ToArea;
                if (visited.Contains(next)) continue;
                if (isAreaBlocked != null && isAreaBlocked(next) && next != toArea) continue;
                // 미설정 SpawnCell(0,0) conn은 사용 불가 — 영역 중심 폴백 회피용 가드
                if (conn.SpawnCell.X == 0 && conn.SpawnCell.Y == 0) continue;
                if (IsConnectionStaticallyLocked(mapId, conn)) continue;

                visited.Add(next);
                parent[next] = (current, conn);

                if (next == toArea)
                {
                    var seq = new List<AreaSeqNode>
                    {
                        new() { Area = toArea, IncomingConn = conn }
                    };
                    var c = toArea;
                    while (parent.TryGetValue(c, out var p))
                    {
                        // p.prev로 거슬러 올라가며, p.prev로 진입한 conn을 찾아야 함
                        AreaConnectionInfo? prevConn = null;
                        if (parent.TryGetValue(p.prev, out var pp)) prevConn = pp.conn;
                        seq.Insert(0, new AreaSeqNode { Area = p.prev, IncomingConn = prevConn });
                        c = p.prev;
                    }
                    return seq;
                }
                queue.Enqueue(next);
            }
        }
        return null;
    }

    /// <summary>
    ///     같은 (fromArea → toArea) 쌍에 여러 conn(west/east 계단 등)이 있을 때
    ///     fromArea 측 도어 셀이 currentCell과 가장 가까운 conn을 선택.
    ///     맨해튼 거리 기준. SpawnCell이 (0,0)인 미설정 conn은 제외.
    /// </summary>
    private static AreaConnectionInfo? PickClosestConnection(MapId mapId, AreaType fromArea, AreaType toArea, Cell currentCell)
    {
        AreaConnectionInfo? best = null;
        int bestDist = int.MaxValue;
        foreach (var conn in GameAreaConnectionData.GetConnections(mapId, fromArea))
        {
            if (conn.ToArea != toArea) continue;
            if (conn.SpawnCell.X == 0 && conn.SpawnCell.Y == 0) continue;
            if (IsConnectionStaticallyLocked(mapId, conn)) continue;
            // exit cell(fromArea 측 도어) = reverse conn의 SpawnCell
            var exit = FindReverseSpawnCell(mapId, conn);
            if (exit == null) continue;
            int dist = Math.Abs(exit.X - currentCell.X) + Math.Abs(exit.Y - currentCell.Y);

            if (dist < bestDist)
            {
                bestDist = dist;
                best = conn;
            }
        }
        return best;
    }

    /// <summary>
    ///     연결이 정적으로 잠긴 문(is_initially_open=0)으로 막혀 있는지.
    ///     매칭별 동적 개폐 상태는 다루지 않는다 — 열쇠 전용 영구 잠금 문의 봇 경로 차단용.
    /// </summary>
    public static bool IsConnectionStaticallyLocked(MapId mapId, AreaConnectionInfo conn)
    {
        var exit = FindReverseSpawnCell(mapId, conn);
        if (exit == null) return false;

        var door = GameDoorData.GetDoorForTransition(conn.FromArea, conn.ToArea, exit, conn.SpawnCell);
        return door is { IsInitiallyOpen: false };
    }

    /// <summary>
    ///     forwardConn(fromA → toA, west/east StairSide 포함)에 짝을 이루는 reverse conn(toA → fromA)의 SpawnCell.
    ///     양방향 같은 도어를 통하므로 fromA 측 도어 셀(= 봇이 walking으로 도달해야 할 출구)을 의미.
    /// </summary>
    private static Cell? FindReverseSpawnCell(MapId mapId, AreaConnectionInfo forwardConn)
    {
        foreach (var rev in GameAreaConnectionData.GetConnections(mapId, forwardConn.ToArea))
        {
            if (rev.ToArea != forwardConn.FromArea) continue;
            if (rev.StairSide != forwardConn.StairSide) continue;
            if (rev.SpawnCell.X == 0 && rev.SpawnCell.Y == 0) continue;
            return rev.SpawnCell;
        }
        return null;
    }

    /// <summary>
    ///     영역 안 셀 그리드 BFS. fromCell → toCell, 영역 바깥 셀은 회피.
    ///     반환 리스트는 fromCell 포함 (호출자가 Skip(1) 처리).
    /// </summary>
    private static List<Cell>? BfsCellsInArea(MapId mapId, AreaType area, Cell fromCell, Cell toCell)
    {
        if (fromCell.Equals(toCell)) return new List<Cell> { fromCell };

        var areaRegions = GameMapData.GetAreas(mapId).Where(r => r.AreaType == area).ToList();
        if (areaRegions.Count == 0) return null;

        var visited = new HashSet<(int, int)> { (fromCell.X, fromCell.Y) };
        var parent = new Dictionary<(int, int), Cell>();
        var queue = new Queue<Cell>();
        queue.Enqueue(fromCell);

        const int maxIterations = 5000; // 영역 셀 수 안전 상한
        int iter = 0;

        while (queue.Count > 0 && iter < maxIterations)
        {
            iter++;
            var current = queue.Dequeue();
            foreach (var neighbor in current.GetAdjacentCells())
            {
                if (visited.Contains((neighbor.X, neighbor.Y))) continue;
                if (!IsWithinArea(areaRegions, neighbor)) continue;
                if (!GameMapData.IsMoveablePosition(mapId, neighbor)) continue;

                visited.Add((neighbor.X, neighbor.Y));
                parent[(neighbor.X, neighbor.Y)] = current;

                if (neighbor.Equals(toCell))
                {
                    var path = new List<Cell> { toCell };
                    var c = toCell;
                    while (parent.TryGetValue((c.X, c.Y), out var p))
                    {
                        path.Insert(0, p);
                        c = p;
                    }
                    var smoothed = SmoothPath(mapId, areaRegions, path);
                    return InsertIsoAxisCorners(mapId, areaRegions, smoothed);
                }
                queue.Enqueue(neighbor);
            }
        }
        return null;
    }

    /// <summary>
    ///     #127 옵션 C: 셀 X축/Y축에 정렬된 L자 경로로 분해.
    ///     #36 InteractableObject.TryComputeIsoAxisWaypoint 동등 — isometric 맵에서 직선 이동이
    ///     사선처럼 보이는 문제 해결. 셀 dx/dy 중 더 큰 축을 먼저 걷고, 코너에서 다음 축으로 이동.
    ///     L 코너 셀이 walkable이 아니면 직선 폴백.
    /// </summary>
    private static List<Cell> InsertIsoAxisCorners(MapId mapId, IReadOnlyList<GameMapData.AreaRegion> areaRegions, List<Cell> smoothed)
    {
        if (smoothed.Count <= 1) return smoothed;

        var result = new List<Cell> { smoothed[0] };
        for (int i = 0; i < smoothed.Count - 1; i++)
        {
            var a = smoothed[i];
            var b = smoothed[i + 1];
            int dx = b.X - a.X;
            int dy = b.Y - a.Y;

            // 이미 한 축에 정렬됨 — 코너 불필요
            if (dx == 0 || dy == 0)
            {
                result.Add(b);
                continue;
            }

            // 더 긴 축을 먼저 걸음 (#36 TryComputeIsoAxisWaypoint와 동일)
            Cell corner = Math.Abs(dx) >= Math.Abs(dy)
                ? new Cell(a.X + dx, a.Y)
                : new Cell(a.X, a.Y + dy);

            // 두 segment(L 양변) 모두 LOS 통과 + 영역 안인지 확인
            if (IsWithinArea(areaRegions, corner)
                && GameMapData.IsMoveablePosition(mapId, corner)
                && HasClearLine(mapId, areaRegions, a, corner)
                && HasClearLine(mapId, areaRegions, corner, b))
            {
                result.Add(corner);
                result.Add(b);
            }
            else
            {
                // L 코너 막힘 — 직선 폴백
                result.Add(b);
            }
        }
        return result;
    }

    /// <summary>
    ///     #127 폴리싱: BFS 결과를 line-of-sight로 평활화.
    ///     셀 그리드는 8방향이라 BFS 최단경로가 staircase 형태가 되어 봇이 좌우로 흔들린다.
    ///     "i에서 j까지 직선으로 이동 가능하면 중간 셀 제거" 규칙으로 turning point만 남김 → 자연스러운 직선 walking.
    /// </summary>
    private static List<Cell> SmoothPath(MapId mapId, IReadOnlyList<GameMapData.AreaRegion> areaRegions, List<Cell> path)
    {
        if (path.Count <= 2) return path;

        var smoothed = new List<Cell> { path[0] };
        int i = 0;
        while (i < path.Count - 1)
        {
            int j = path.Count - 1;
            while (j > i + 1)
            {
                if (HasClearLine(mapId, areaRegions, path[i], path[j])) break;
                j--;
            }
            smoothed.Add(path[j]);
            i = j;
        }
        return smoothed;
    }

    private static bool IsWithinArea(IReadOnlyList<GameMapData.AreaRegion> areaRegions, Cell cell)
    {
        return areaRegions.Any(region => region.Contains(cell));
    }

    /// <summary>
    ///     Bresenham 라인 트레이싱으로 from → to 사이 모든 중간 셀이 walkable + 영역 안에 있는지 확인.
    /// </summary>
    private static bool HasClearLine(MapId mapId, IReadOnlyList<GameMapData.AreaRegion> areaRegions, Cell from, Cell to)
    {
        int dx = Math.Abs(to.X - from.X);
        int dy = Math.Abs(to.Y - from.Y);
        int sx = from.X < to.X ? 1 : -1;
        int sy = from.Y < to.Y ? 1 : -1;
        int err = dx - dy;
        int x = from.X, y = from.Y;
        const int maxSteps = 200; // 안전 상한
        int steps = 0;

        while (steps++ < maxSteps)
        {
            var c = new Cell(x, y);
            if (!IsWithinArea(areaRegions, c)) return false;
            if (!GameMapData.IsMoveablePosition(mapId, c)) return false;
            if (x == to.X && y == to.Y) return true;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 < dx) { err += dx; y += sy; }
        }
        return false;
    }
}
