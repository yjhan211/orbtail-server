using System;
using System.Collections.Generic;
using network.common.data.models;

namespace network.common.data.helpers
{
    /// <summary>
    /// Validates every grid cell crossed by a movement segment. This prevents a
    /// destination-only passability check from tunnelling through a one-cell wall.
    /// </summary>
    public static class MapTraversal
    {
        public static bool IsTraversable(MapId mapId, Vector3f from, Vector3f to)
        {
            var start = MapCoordinateConverter.WorldToCell(mapId, from);
            var destination = MapCoordinateConverter.WorldToCell(mapId, to);
            return GameMapData.IsMoveablePosition(mapId, destination) &&
                IsTraversable(start, destination, cell => GameMapData.IsMoveablePosition(mapId, cell));
        }

        public static bool IsTraversable(
            Cell start,
            Cell destination,
            Func<Cell, bool> canOccupy,
            Func<Cell, Cell, bool>? canCross = null)
        {
            if (start == null) throw new ArgumentNullException(nameof(start));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (canOccupy == null) throw new ArgumentNullException(nameof(canOccupy));

            foreach (var step in GetSteps(start, destination))
            {
                if (step.Horizontal != null && step.Vertical != null)
                {
                    bool horizontalOpen = CanEnter(step.From, step.Horizontal, canOccupy, canCross);
                    bool verticalOpen = CanEnter(step.From, step.Vertical, canOccupy, canCross);
                    if (!horizontalOpen && !verticalOpen) return false;
                }
                if (!CanEnter(step.From, step.To, canOccupy, canCross)) return false;
            }
            return true;
        }

        /// <summary>구간이 지나는 셀과 대각선 모서리 후보를 제공한다. 통행 정책은 호출자가 판단한다.</summary>
        public static IEnumerable<TraversalStep> GetSteps(Cell start, Cell destination)
        {
            if (start == null) throw new ArgumentNullException(nameof(start));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int x = start.X;
            int y = start.Y;
            int deltaX = destination.X - x;
            int deltaY = destination.Y - y;
            int stepX = Math.Sign(deltaX);
            int stepY = Math.Sign(deltaY);
            int countX = Math.Abs(deltaX);
            int countY = Math.Abs(deltaY);
            int movedX = 0;
            int movedY = 0;

            while (movedX < countX || movedY < countY)
            {
                long xBoundary = (1L + 2L * movedX) * countY;
                long yBoundary = (1L + 2L * movedY) * countX;

                if (xBoundary == yBoundary && movedX < countX && movedY < countY)
                {
                    // 대각선에서는 양옆 셀도 제공한다. 둘 다 막혔는지는 통행 판정에서 확인한다.
                    var current = new Cell(x, y);
                    var horizontal = new Cell(x + stepX, y);
                    var vertical = new Cell(x, y + stepY);

                    x += stepX;
                    y += stepY;
                    movedX++;
                    movedY++;
                    yield return new TraversalStep(current, new Cell(x, y), horizontal, vertical);
                }
                else if (xBoundary < yBoundary && movedX < countX)
                {
                    var current = new Cell(x, y);
                    x += stepX;
                    movedX++;
                    yield return new TraversalStep(current, new Cell(x, y));
                }
                else
                {
                    var current = new Cell(x, y);
                    y += stepY;
                    movedY++;
                    yield return new TraversalStep(current, new Cell(x, y));
                }
            }
        }

        private static bool CanEnter(
            Cell from,
            Cell to,
            Func<Cell, bool> canOccupy,
            Func<Cell, Cell, bool>? canCross)
        {
            return canOccupy(to) && (canCross == null || canCross(from, to));
        }
        public readonly struct TraversalStep
        {
            public Cell From { get; }
            public Cell To { get; }
            public Cell? Horizontal { get; }
            public Cell? Vertical { get; }

            public TraversalStep(Cell from, Cell to, Cell? horizontal = null, Cell? vertical = null)
            {
                From = from;
                To = to;
                Horizontal = horizontal;
                Vertical = vertical;
            }
        }
    }
}
