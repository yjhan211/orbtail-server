using System;
using System.Collections.Generic;
using System.Linq;
using network.common;
using network.common.data.models;

namespace network.common.data
{
    public static class MapPathfinder
    {
        private const int DoorClearanceStepCount = 2;
        public class Step
        {
            public Cell Cell { get; set; } = new(0, 0);
            public AreaType Area { get; set; }
            public bool IsAreaTransition { get; set; }
        }

        public static List<Step>? FindPath(MapId mapId, AreaType fromArea, Cell fromCell, AreaType toArea, Cell toCell, Func<AreaType, bool>? isAreaBlocked = null)
        {
            var areaSeq = BfsAreaGraph(mapId, fromArea, toArea, isAreaBlocked);
            if (areaSeq == null) return null;

            var path = new List<Step>();
            var currentCell = fromCell;

            for (int i = 0; i < areaSeq.Count - 1; i++)
            {
                var fromA = areaSeq[i].Area;
                var toA = areaSeq[i + 1].Area;
                var forwardConn = PickClosestConnection(mapId, fromA, toA, currentCell) ?? areaSeq[i + 1].IncomingConn;

                Cell? exitCell = null;
                if (forwardConn != null)
                {
                    exitCell = FindReverseSpawnCell(mapId, forwardConn);
                }
                if (exitCell == null)
                {
                    exitCell = GameMapData.GetAreaSpawnCell(mapId, fromA);
                }
                exitCell = SnapToWalkableInArea(mapId, fromA, exitCell);

                var cellPath = BfsCellsInArea(mapId, fromA, currentCell, exitCell);
                if (cellPath == null)
                {
                    return null;
                }

                foreach (var c in cellPath.Skip(1))
                {
                    path.Add(new Step { Cell = c, Area = fromA, IsAreaTransition = false });
                }

                var entryCell = forwardConn?.SpawnCell ?? GameMapData.GetAreaSpawnCell(mapId, toA);
                entryCell = SnapToWalkableInArea(mapId, toA, entryCell);
                path.Add(new Step
                {
                    Cell = entryCell,
                    Area = toA,
                    IsAreaTransition = true
                });
                currentCell = entryCell;

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

                    toCell = currentCell;
                }
            }

            var finalPath = BfsCellsInArea(mapId, toArea, currentCell, toCell);
            if (finalPath == null)
            {
                return null;
            }

            foreach (var c in finalPath.Skip(1))
            {
                path.Add(new Step { Cell = c, Area = toArea, IsAreaTransition = false });
            }
            return path;
        }

        private static Cell SnapToWalkableInArea(MapId mapId, AreaType area, Cell cell)
        {
            bool IsGood(Cell candidate) =>
                GameMapData.IsMoveablePosition(mapId, candidate) &&
                GameMapData.GetCurrentArea(mapId, candidate) == area;

            if (IsGood(cell))
                return cell;

            ReadOnlySpan<(int Dx, int Dy)> offsets = stackalloc (int Dx, int Dy)[]
            {
                (0, -1), (0, 1), (-1, 0), (1, 0),
                (-1, -1), (1, -1), (-1, 1), (1, 1)
            };
            foreach (var (dx, dy) in offsets)
            {
                var candidate = new Cell(cell.X + dx, cell.Y + dy);
                if (IsGood(candidate))
                    return candidate;
            }

            return cell;
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
                {
                    break;
                }

                result.Add(candidate);
                current = candidate;
            }

            return result;
        }

        private class AreaSeqNode
        {
            public AreaType Area { get; set; }
            public AreaConnectionInfo? IncomingConn { get; set; }
        }

        private static List<AreaSeqNode>? BfsAreaGraph(MapId mapId, AreaType fromArea, AreaType toArea, Func<AreaType, bool>? isAreaBlocked)
        {
            if (fromArea == toArea)
            {
                return new List<AreaSeqNode> { new() { Area = fromArea, IncomingConn = null } };
            }

            var visited = new HashSet<AreaType> { fromArea };
            var parent = new Dictionary<AreaType, (AreaType prev, AreaConnectionInfo conn)>();
            var queue = new Queue<AreaType>();
            queue.Enqueue(fromArea);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var conn in GameAreaConnectionData.GetConnections(mapId, current))
                {
                    var next = conn.ToArea;
                    if (visited.Contains(next))
                    {
                        continue;
                    }
                    if (isAreaBlocked != null && isAreaBlocked(next) && next != toArea)
                    {
                        continue;
                    }

                    if (conn.SpawnCell.X == 0 && conn.SpawnCell.Y == 0)
                    {
                        continue;
                    }

                    if (IsConnectionStaticallyLocked(mapId, conn))
                    {
                        continue;
                    }

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
                            AreaConnectionInfo? prevConn = null;
                            if (parent.TryGetValue(p.prev, out var pp))
                            {
                                prevConn = pp.conn;
                            }
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

        private static AreaConnectionInfo? PickClosestConnection(MapId mapId, AreaType fromArea, AreaType toArea, Cell currentCell)
        {
            AreaConnectionInfo? best = null;
            int bestDist = int.MaxValue;
            foreach (var conn in GameAreaConnectionData.GetConnections(mapId, fromArea))
            {
                if (conn.ToArea != toArea)
                {
                    continue;
                }
                if (conn.SpawnCell.X == 0 && conn.SpawnCell.Y == 0)
                {
                    continue;
                }

                if (IsConnectionStaticallyLocked(mapId, conn))
                {
                    continue;
                }
                var exit = FindReverseSpawnCell(mapId, conn);
                if (exit == null)
                {
                    continue;
                }
                int dist = Math.Abs(exit.X - currentCell.X) + Math.Abs(exit.Y - currentCell.Y);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = conn;
                }
            }
            return best;
        }

        public static bool IsConnectionStaticallyLocked(MapId mapId, AreaConnectionInfo conn)
        {
            var exit = FindReverseSpawnCell(mapId, conn);
            if (exit == null)
            {
                return false;
            }

            var door = GameDoorData.GetDoorForTransition(conn.FromArea, conn.ToArea, exit, conn.SpawnCell);
            if (door == null)
            {
                return false;
            }

            if (GameInteractableData.IsGaugeGatedDoor(door.DoorId))
            {
                return false;
            }
            return !door.IsInitiallyOpen;
        }

        private static Cell? FindReverseSpawnCell(MapId mapId, AreaConnectionInfo forwardConn)
        {
            Cell? best = null;
            int bestDistance = int.MaxValue;
            foreach (var rev in GameAreaConnectionData.GetConnections(mapId, forwardConn.ToArea))
            {
                if (rev.ToArea != forwardConn.FromArea)
                {
                    continue;
                }

                if (rev.StairSide != forwardConn.StairSide)
                {
                    continue;
                }

                if (rev.SpawnCell.X == 0 && rev.SpawnCell.Y == 0)
                {
                    continue;
                }
                int distance = Math.Abs(rev.SpawnCell.X - forwardConn.SpawnCell.X) + Math.Abs(rev.SpawnCell.Y - forwardConn.SpawnCell.Y);
                if (distance >= bestDistance)
                {
                    continue;
                }
                bestDistance = distance;
                best = rev.SpawnCell;
            }

            return best;
        }

        private static List<Cell>? BfsCellsInArea(MapId mapId, AreaType area, Cell fromCell, Cell toCell)
        {
            if (fromCell.Equals(toCell))
            {
                return new List<Cell> { fromCell };
            }

            var areaRegions = GameMapData.GetAreas(mapId).Where(r => r.AreaType == area).ToList();
            if (areaRegions.Count == 0)
            {
                return null;
            }

            var visited = new HashSet<(int, int)> { (fromCell.X, fromCell.Y) };
            var parent = new Dictionary<(int, int), Cell>();
            var queue = new Queue<Cell>();
            queue.Enqueue(fromCell);

            const int maxIterations = 20000;
            int iter = 0;

            while (queue.Count > 0 && iter < maxIterations)
            {
                iter++;
                var current = queue.Dequeue();
                foreach (var neighbor in current.GetAdjacentCells())
                {
                    if (visited.Contains((neighbor.X, neighbor.Y)))
                    {
                        continue;
                    }

                    if (!IsWithinArea(areaRegions, neighbor))
                    {
                        continue;
                    }

                    if (!GameMapData.IsMoveablePosition(mapId, neighbor))
                    {
                        continue;
                    }

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

        private static List<Cell> InsertIsoAxisCorners(MapId mapId, IReadOnlyList<GameMapData.AreaRegion> areaRegions, List<Cell> smoothed)
        {
            if (smoothed.Count <= 1)
            {
                return smoothed;
            }

            var result = new List<Cell> { smoothed[0] };
            for (int i = 0; i < smoothed.Count - 1; i++)
            {
                var a = smoothed[i];
                var b = smoothed[i + 1];
                int dx = b.X - a.X;
                int dy = b.Y - a.Y;
                if (dx == 0 || dy == 0)
                {
                    result.Add(b);
                    continue;
                }

                var corner = Math.Abs(dx) >= Math.Abs(dy) ? new Cell(a.X + dx, a.Y) : new Cell(a.X, a.Y + dy);
                if (IsWithinArea(areaRegions, corner) && GameMapData.IsMoveablePosition(mapId, corner) && HasClearLine(mapId, areaRegions, a, corner) && HasClearLine(mapId, areaRegions, corner, b))
                {
                    result.Add(corner);
                    result.Add(b);
                }
                else
                {
                    result.Add(b);
                }
            }
            return result;
        }

        private static List<Cell> SmoothPath(MapId mapId, IReadOnlyList<GameMapData.AreaRegion> areaRegions, List<Cell> path)
        {
            if (path.Count <= 2)
            {
                return path;
            }

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

        private static bool HasClearLine(MapId mapId, IReadOnlyList<GameMapData.AreaRegion> areaRegions, Cell from, Cell to)
        {
            float deltaX = to.X - from.X;
            float deltaY = to.Y - from.Y;
            int samples = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(deltaX), MathF.Abs(deltaY)) * 4f));
            if (samples > 400)
            {
                return false;
            }

            for (int sample = 0; sample <= samples; sample++)
            {
                float t = sample / (float)samples;
                var cell = new Cell((int)MathF.Round(from.X + deltaX * t),
                    (int)MathF.Round(from.Y + deltaY * t));
                if (!IsWithinArea(areaRegions, cell))
                {
                    return false;
                }

                if (!GameMapData.IsMoveablePosition(mapId, cell))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
