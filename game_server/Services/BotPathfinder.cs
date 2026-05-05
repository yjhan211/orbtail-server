using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     #127: 봇 셀 단위 walking을 위한 pathfinding.
///     - 영역 그래프 BFS (어느 영역들을 거칠지)
///     - 영역별 셀 그리드 BFS (영역 안에서 어디로 갈지)
///     - 영역 경계 통과 시 GameAreaConnectionData.GetSpawnCell로 텔레포트 (실제 플레이어 HandleAreaMove 동등)
/// </summary>
public static class BotPathfinder
{
    /// <summary>경로의 한 단계. 셀 + 그 셀이 속한 영역 + 영역 전환 여부.</summary>
    public class Step
    {
        public Cell Cell { get; set; } = new(0, 0);
        public AreaType Area { get; set; }
        /// <summary>true면 이 셀로 텔레포트 진입 (영역 경계 통과). false면 인접 셀로 walk.</summary>
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
        // 1) 영역 시퀀스 BFS (인접 그래프)
        var areaSeq = BfsAreaGraph(mapId, fromArea, toArea, isAreaBlocked);
        if (areaSeq == null) return null;

        var path = new List<Step>();
        var currentCell = fromCell;

        // 2) 각 영역 사이 walk + 경계 통과
        for (int i = 0; i < areaSeq.Count - 1; i++)
        {
            var fromA = areaSeq[i];
            var toA = areaSeq[i + 1];

            // 현재 영역 측 도어 셀 = 반대 방향 SpawnCell (toA → fromA 연결의 SpawnCell이 fromA에 있음)
            var exitCell = GameAreaConnectionData.GetSpawnCell(mapId, toA, fromA);
            if (exitCell == null)
            {
                // 미설정 — 영역 중심 폴백
                exitCell = GameMapData.GetAreaSpawnCell(mapId, fromA);
            }

            // 현재 셀 → 출구 셀 (영역 안에서 walk)
            var cellPath = BfsCellsInArea(mapId, fromA, currentCell, exitCell);
            if (cellPath == null)
            {
                // walk 실패 — 다음 영역으로 그냥 텔레포트 시도 (드물게 발생)
            }
            else
            {
                foreach (var c in cellPath.Skip(1)) // 시작 셀(현재 위치) 제외
                    path.Add(new Step { Cell = c, Area = fromA, IsAreaTransition = false });
            }

            // 영역 경계 통과 (텔레포트)
            var entryCell = GameAreaConnectionData.GetSpawnCell(mapId, fromA, toA);
            if (entryCell == null)
            {
                entryCell = GameMapData.GetAreaSpawnCell(mapId, toA);
            }
            path.Add(new Step
            {
                Cell = entryCell,
                Area = toA,
                IsAreaTransition = true,
                FromAreaForTransition = fromA
            });
            currentCell = entryCell;
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

    /// <summary>
    ///     영역 그래프 BFS. fromArea → toArea 최단 영역 시퀀스.
    ///     isAreaBlocked가 true 반환하는 영역은 회피.
    /// </summary>
    private static List<AreaType>? BfsAreaGraph(MapId mapId, AreaType fromArea, AreaType toArea,
        Func<AreaType, bool>? isAreaBlocked)
    {
        if (fromArea == toArea) return new List<AreaType> { fromArea };

        var visited = new HashSet<AreaType> { fromArea };
        var parent = new Dictionary<AreaType, AreaType>();
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

                visited.Add(next);
                parent[next] = current;

                if (next == toArea)
                {
                    // 경로 복원
                    var seq = new List<AreaType> { toArea };
                    var c = toArea;
                    while (parent.TryGetValue(c, out var p))
                    {
                        seq.Insert(0, p);
                        c = p;
                    }
                    return seq;
                }
                queue.Enqueue(next);
            }
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

        var areaRegion = GameMapData.GetAreas(mapId).FirstOrDefault(r => r.AreaType == area);
        if (areaRegion == null) return null;

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
                if (!areaRegion.Contains(neighbor)) continue;
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
                    var smoothed = SmoothPath(mapId, areaRegion, path);
                    return InsertIsoAxisCorners(mapId, areaRegion, smoothed);
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
    private static List<Cell> InsertIsoAxisCorners(MapId mapId, GameMapData.AreaRegion area, List<Cell> smoothed)
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
            if (area.Contains(corner)
                && GameMapData.IsMoveablePosition(mapId, corner)
                && HasClearLine(mapId, area, a, corner)
                && HasClearLine(mapId, area, corner, b))
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
    private static List<Cell> SmoothPath(MapId mapId, GameMapData.AreaRegion area, List<Cell> path)
    {
        if (path.Count <= 2) return path;

        var smoothed = new List<Cell> { path[0] };
        int i = 0;
        while (i < path.Count - 1)
        {
            int j = path.Count - 1;
            while (j > i + 1)
            {
                if (HasClearLine(mapId, area, path[i], path[j])) break;
                j--;
            }
            smoothed.Add(path[j]);
            i = j;
        }
        return smoothed;
    }

    /// <summary>
    ///     Bresenham 라인 트레이싱으로 from → to 사이 모든 중간 셀이 walkable + 영역 안에 있는지 확인.
    /// </summary>
    private static bool HasClearLine(MapId mapId, GameMapData.AreaRegion area, Cell from, Cell to)
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
            if (!area.Contains(c)) return false;
            if (!GameMapData.IsMoveablePosition(mapId, c)) return false;
            if (x == to.X && y == to.Y) return true;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 < dx) { err += dx; y += sy; }
        }
        return false;
    }
}
