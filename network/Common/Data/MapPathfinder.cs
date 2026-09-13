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

        public static Vector3f AdvanceRoute(MapId mapId, Vector3f position, IReadOnlyList<Vector3f> route,
            ref int index, float distanceBudget, Func<int, bool>? isDoorOpen = null,
            Func<Vector3f, bool>? canEnter = null)
        {
            var current = new Vector3f(position.X, position.Y, 0f);
            while (distanceBudget > 0f && index < route.Count)
            {
                var target = route[index];
                float dx = target.X - current.X;
                float dy = target.Y - current.Y;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance == 0f)
                {
                    index++;
                    continue;
                }

                var fromCell = MapCoordinateConverter.WorldToCell(mapId, current);
                var targetCell = MapCoordinateConverter.WorldToCell(mapId, target);
                var fromArea = GameMapData.GetCurrentArea(mapId, fromCell);
                var targetArea = GameMapData.GetCurrentArea(mapId, targetCell);
                var door = GameDoorData.GetDoorForTransition(fromArea, targetArea, fromCell, targetCell);
                if (door != null && isDoorOpen != null && !isDoorOpen(door.DoorId))
                {
                    break;
                }
                if (canEnter != null && !canEnter(target))
                {
                    break;
                }
                if (!IsSegmentWalkable(mapId, current, target))
                {
                    break;
                }

                float step = Math.Min(distanceBudget, distance);
                var next = Vector3f.MoveTowardsXY(current, target, step);
                if (canEnter != null && !canEnter(next))
                {
                    break;
                }
                current = next;
                distanceBudget -= step;
                if (step == distance)
                {
                    current = new Vector3f(target.X, target.Y, 0f);
                    index++;
                }
            }
            return current;
        }

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

        private const float RouteSampleStep = 0.35f;
        private const int DoorwayBlockedSampleTolerance = 10;

        public static bool TryPlanRoute(MapId mapId, AreaType fromArea, Vector3f from, AreaType toArea, Vector3f to, Func<AreaType, bool>? isAreaBlocked, out List<Vector3f> route)
        {
            route = null!;
            var steps = FindPath(mapId, fromArea, MapCoordinateConverter.WorldToCell(mapId, from), toArea, MapCoordinateConverter.WorldToCell(mapId, to), isAreaBlocked);
            if (steps == null || steps.Count == 0)
            {
                return false;
            }

            var planned = new List<Vector3f>(steps.Count + 2) { from };
            foreach (var step in steps)
            {
                planned.Add(MapCoordinateConverter.CellToWorld(mapId, step.Cell));
            }
            planned.Add(to);

            for (int index = 1; index < planned.Count; index++)
            {
                if (!IsSegmentWalkable(mapId, planned[index - 1], planned[index]))
                {
                    return false;
                }
            }
            planned.RemoveAt(0);
            route = planned;
            return true;
        }

        public static bool IsSegmentWalkable(MapId mapId, Vector3f from, Vector3f to)
        {
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            int samples = Math.Max(1, (int)MathF.Ceiling(distance / RouteSampleStep));
            int blockedRun = 0;
            for (int index = 1; index <= samples; index++)
            {
                float t = index / (float)samples;
                var point = new Vector3f(from.X + dx * t, from.Y + dy * t, 0f);
                if (GameMapData.IsMoveablePosition(mapId, MapCoordinateConverter.WorldToCell(mapId, point)))
                {
                    blockedRun = 0;
                    continue;
                }

                if (++blockedRun > DoorwayBlockedSampleTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsWalkableInArea(MapId mapId, Vector3f position, AreaType area)
        {
            var cell = MapCoordinateConverter.WorldToCell(mapId, position);
            return GameMapData.IsMoveablePosition(mapId, cell) && GameMapData.GetCurrentArea(mapId, cell) == area;
        }

        public static Vector3f ClampToAreaWalkable(MapId mapId, Vector3f position, Vector3f center, AreaType area)
        {
            if (IsWalkableInArea(mapId, position, area))
            {
                return position;
            }

            for (float t = 0.1f; t <= 1f; t += 0.1f)
            {
                var candidate = new Vector3f(position.X + (center.X - position.X) * t, position.Y + (center.Y - position.Y) * t, 0f);
                if (IsWalkableInArea(mapId, candidate, area))
                {
                    return candidate;
                }
            }

            return center;
        }

        public static Vector3f ClampToWalkable(MapId mapId, Vector3f position, Vector3f center)
        {
            if (GameMapData.IsMoveablePosition(mapId, MapCoordinateConverter.WorldToCell(mapId, position)))
            {
                return position;
            }

            for (float t = 0.1f; t <= 1f; t += 0.1f)
            {
                var candidate = new Vector3f(position.X + (center.X - position.X) * t, position.Y + (center.Y - position.Y) * t, 0f);
                if (GameMapData.IsMoveablePosition(mapId, MapCoordinateConverter.WorldToCell(mapId, candidate)))
                {
                    return candidate;
                }
            }
            return center;
        }

        /// <summary>구역 경계에서 margin 셀만큼 안쪽으로 셀을 당긴다. 구역이 margin×2보다 좁으면 그대로 둔다.</summary>
        public static Cell InsetCellFromAreaEdge(MapId mapId, Cell cell, AreaType area, int margin)
        {
            GameMapData.AreaRegion? region = null;
            foreach (var candidate in GameMapData.GetAreas(mapId))
            {
                if (candidate.AreaType == area)
                {
                    region = candidate;
                    break;
                }
            }
            if (region == null)
            {
                return cell;
            }

            int minX = region.Start.X + margin;
            int maxX = region.End.X - margin;
            int minY = region.Start.Y + margin;
            int maxY = region.End.Y - margin;
            if (minX > maxX || minY > maxY)
            {
                return cell;
            }

            int insetX = Math.Clamp(cell.X, minX, maxX);
            int insetY = Math.Clamp(cell.Y, minY, maxY);
            return insetX == cell.X && insetY == cell.Y ? cell : new Cell(insetX, insetY);
        }
    }
}
