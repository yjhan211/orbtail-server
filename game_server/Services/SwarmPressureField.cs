using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     #217 자기장: 운동장 기준 보행 거리 필드. 구역 단위 폐쇄 대신, 안전 거리 밖의
///     참가자에게 초과 거리에 비례한 오염을 준다 — 밀리는 방향이 곧 걸어야 하는 방향.
///     원형이 아니라 실제 보행 거리로 재므로 벽 너머가 안전해 보이는 거짓 신호가 없다.
///     거리 맵은 서버 수명 동안 불변이라 시작 시 한 번 플러드필한다 (잠긴 문 셀은 벽 취급).
/// </summary>
public static class SwarmPressureField
{
    private static readonly object InitLock = new();
    private static Dictionary<(int X, int Y), int> _distanceByCell;
    private static Dictionary<AreaType, int> _minDistanceByArea;
    private static int _maxDistance;

    /// <summary>맵에서 운동장까지 가장 먼 보행 거리 — 안전 거리 수축의 시작값.</summary>
    public static int MaxDistance
    {
        get
        {
            EnsureInitialized();
            return _maxDistance;
        }
    }

    /// <summary>운동장까지 보행 거리. 도달 불가 셀은 최대 거리로 취급한다.</summary>
    public static int GetDistance(Cell cell)
    {
        EnsureInitialized();
        return _distanceByCell.GetValueOrDefault((cell.X, cell.Y), _maxDistance);
    }

    /// <summary>
    ///     구역에서 운동장에 가장 가까운 셀의 거리.
    ///     이 값이 안전 거리를 넘으면 구역 전체가 경계 밖이다.
    /// </summary>
    public static int GetAreaMinDistance(AreaType area)
    {
        EnsureInitialized();
        return _minDistanceByArea.GetValueOrDefault(area, int.MaxValue);
    }

    public static IReadOnlyCollection<AreaType> GetKnownAreas()
    {
        EnsureInitialized();
        return _minDistanceByArea.Keys;
    }

    private static void EnsureInitialized()
    {
        if (_distanceByCell != null) return;
        lock (InitLock)
        {
            if (_distanceByCell != null) return;

            // 잠긴 문(초기 폐문)은 벽이다 — 뒷문 너머가 가깝게 측정되면 안 된다.
            var blockedCells = GameDoorData.GetAll()
                .Where(door => !door.IsInitiallyOpen)
                .Select(door => ((int)door.PositionX, (int)door.PositionY))
                .ToHashSet();

            // 구역 경계는 열린 문 주변(체비셰프 1)에서만 넘을 수 있다 — 타일 데이터에
            // 문 없이 맞닿은 경계가 있어도 위상(문 그래프)이 거리의 기준이 되게 한다.
            var openDoorNeighborhood = new HashSet<(int X, int Y)>();
            foreach (var door in GameDoorData.GetAll().Where(door => door.IsInitiallyOpen))
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        openDoorNeighborhood.Add(((int)door.PositionX + dx, (int)door.PositionY + dy));

            var distances = new Dictionary<(int X, int Y), int>();
            var queue = new Queue<Cell>();

            // 운동장 내부 전체를 거리 0으로 시드 — 운동장 안 어디든 완전한 안전지대다.
            var groundSeed = GameMapData.GetAreaSpawnCell(MapId.School, AreaType.Ground);
            var groundQueue = new Queue<Cell>();
            groundQueue.Enqueue(groundSeed);
            distances[(groundSeed.X, groundSeed.Y)] = 0;
            while (groundQueue.Count > 0)
            {
                var cell = groundQueue.Dequeue();
                queue.Enqueue(cell);
                foreach (var next in cell.GetAdjacentCells())
                {
                    if (distances.ContainsKey((next.X, next.Y)) ||
                        !IsWalkable(next, blockedCells) ||
                        !IsStepAllowed(cell, next, openDoorNeighborhood) ||
                        GameMapData.GetCurrentArea(MapId.School, next) != AreaType.Ground)
                        continue;
                    distances[(next.X, next.Y)] = 0;
                    groundQueue.Enqueue(next);
                }
            }

            // 운동장 밖으로 확장 — 표준 BFS.
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                int distance = distances[(cell.X, cell.Y)];
                foreach (var next in cell.GetAdjacentCells())
                {
                    if (distances.ContainsKey((next.X, next.Y)) ||
                        !IsWalkable(next, blockedCells) ||
                        !IsStepAllowed(cell, next, openDoorNeighborhood))
                        continue;
                    distances[(next.X, next.Y)] = distance + 1;
                    queue.Enqueue(next);
                }
            }

            var minByArea = new Dictionary<AreaType, int>();
            int maxDistance = 1;
            foreach (var ((x, y), distance) in distances)
            {
                if (distance > maxDistance) maxDistance = distance;
                var area = GameMapData.GetCurrentArea(MapId.School, new Cell(x, y));
                if (area == AreaType.None) continue;
                if (!minByArea.TryGetValue(area, out int currentMin) || distance < currentMin)
                    minByArea[area] = distance;
            }

            _minDistanceByArea = minByArea;
            _maxDistance = maxDistance;
            _distanceByCell = distances;
        }
    }

    private static bool IsWalkable(Cell cell, HashSet<(int, int)> blockedCells)
    {
        return !blockedCells.Contains((cell.X, cell.Y)) &&
               GameMapData.IsMoveablePosition(MapId.School, cell);
    }

    private static bool IsStepAllowed(Cell from, Cell to, HashSet<(int X, int Y)> openDoorNeighborhood)
    {
        // 대각 이동은 모서리 끼임 금지 — 문 셀을 대각으로 스치는 누수를 막는다.
        int dx = to.X - from.X;
        int dy = to.Y - from.Y;
        if (dx != 0 && dy != 0 &&
            (!GameMapData.IsMoveablePosition(MapId.School, new Cell(from.X + dx, from.Y)) ||
             !GameMapData.IsMoveablePosition(MapId.School, new Cell(from.X, from.Y + dy))))
            return false;

        // 같은 구역 안에서는 자유 이동, 구역 경계는 열린 문 주변에서만.
        if (GameMapData.GetCurrentArea(MapId.School, from) == GameMapData.GetCurrentArea(MapId.School, to))
            return true;
        return openDoorNeighborhood.Contains((from.X, from.Y)) ||
               openDoorNeighborhood.Contains((to.X, to.Y));
    }
}
