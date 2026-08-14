using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

// #227 봇 벽 관통 가드: 구역 전환 홉(출구 셀 → 입구 셀)은 직선 이동이므로, 짝지어진
// 출구·입구는 문턱 하나를 사이에 둔 인접 셀이어야 한다. 이중 문(운동장↔정크장·복도)의
// 서문 출구·동문 입구 오짝(첫 행 매칭)은 벽 밴드 34셀 직선 관통을 만들었다.
public class TransitionHopDiagnosticTests
{
    [Fact]
    public void AllSchoolTransitionHops_AreAdjacentDoorCrossings()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var failures = new List<string>();
        foreach (AreaType from in Enum.GetValues<AreaType>())
        {
            var connections = GameAreaConnectionData.GetConnections(MapId.School, from);
            if (connections == null)
                continue;
            foreach (var connection in connections)
            {
                if (connection.SpawnCell.X == 0 && connection.SpawnCell.Y == 0)
                    continue;

                // BotPathfinder.FindReverseSpawnCell과 같은 최근접 짝 매칭을 미러링한다.
                Cell exit = null;
                int bestDistance = int.MaxValue;
                foreach (var rev in GameAreaConnectionData.GetConnections(MapId.School, connection.ToArea))
                {
                    if (rev.ToArea != connection.FromArea || rev.StairSide != connection.StairSide ||
                        (rev.SpawnCell.X == 0 && rev.SpawnCell.Y == 0))
                        continue;
                    int distance = Math.Abs(rev.SpawnCell.X - connection.SpawnCell.X) +
                                   Math.Abs(rev.SpawnCell.Y - connection.SpawnCell.Y);
                    if (distance >= bestDistance)
                        continue;
                    bestDistance = distance;
                    exit = rev.SpawnCell;
                }

                if (exit == null)
                {
                    failures.Add(
                        $"{connection.FromArea}->{connection.ToArea}({connection.StairSide}): 역방향 conn 없음");
                    continue;
                }

                var snappedExit = Snap(connection.FromArea, exit);
                var entry = Snap(connection.ToArea, connection.SpawnCell);
                int chebyshev = Math.Max(
                    Math.Abs(entry.X - snappedExit.X), Math.Abs(entry.Y - snappedExit.Y));

                // 문턱(벽 밴드) 1셀을 사이에 둔 통과까지가 공인 문 통과다.
                if (chebyshev > 2)
                    failures.Add(
                        $"{connection.FromArea}->{connection.ToArea}({connection.StairSide}): " +
                        $"출구({snappedExit.X},{snappedExit.Y})와 입구({entry.X},{entry.Y})가 " +
                        $"인접하지 않다 (cheb={chebyshev}) — 전환 직선 홉이 벽을 관통한다");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static Cell Snap(AreaType area, Cell cell)
    {
        bool IsGood(Cell candidate) =>
            GameMapData.IsMoveablePosition(MapId.School, candidate) &&
            GameMapData.GetCurrentArea(MapId.School, candidate) == area;

        if (IsGood(cell))
            return cell;
        ReadOnlySpan<(int Dx, int Dy)> offsets =
        [
            (0, -1), (0, 1), (-1, 0), (1, 0),
            (-1, -1), (1, -1), (-1, 1), (1, 1)
        ];
        foreach (var (dx, dy) in offsets)
        {
            var candidate = new Cell(cell.X + dx, cell.Y + dy);
            if (IsGood(candidate))
                return candidate;
        }

        return cell;
    }

    private static string FindNetworkBasePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "network");
            if (Directory.Exists(Path.Combine(candidate, "Common", "csv")))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("network base path not found");
    }
}
