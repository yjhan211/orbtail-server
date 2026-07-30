using System;
using network.common.data.models;

namespace network.common.data.helpers
{
    /// <summary>
    /// Validates every grid cell crossed by a movement segment. This prevents a
    /// destination-only passability check from tunnelling through a one-cell wall.
    /// </summary>
    public static class GridMovementTraversal
    {
        public static bool IsTraversable(
            Cell start,
            Cell destination,
            Func<Cell, bool> canOccupy,
            Func<Cell, Cell, bool> canCross = null)
        {
            if (start == null) throw new ArgumentNullException(nameof(start));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (canOccupy == null) throw new ArgumentNullException(nameof(canOccupy));

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
                    // Allow movement around a single blocked corner, such as the
                    // edge of a doorway. Only reject squeezing diagonally between
                    // two blocked side cells; the destination is checked below.
                    var current = new Cell(x, y);
                    var horizontal = new Cell(x + stepX, y);
                    var vertical = new Cell(x, y + stepY);
                    bool horizontalOpen = CanEnter(current, horizontal, canOccupy, canCross);
                    bool verticalOpen = CanEnter(current, vertical, canOccupy, canCross);
                    if (!horizontalOpen && !verticalOpen)
                        return false;

                    x += stepX;
                    y += stepY;
                    movedX++;
                    movedY++;
                }
                else if (xBoundary < yBoundary && movedX < countX)
                {
                    var current = new Cell(x, y);
                    x += stepX;
                    movedX++;
                    if (!CanEnter(current, new Cell(x, y), canOccupy, canCross)) return false;
                    continue;
                }
                else
                {
                    var current = new Cell(x, y);
                    y += stepY;
                    movedY++;
                    if (!CanEnter(current, new Cell(x, y), canOccupy, canCross)) return false;
                    continue;
                }

                if (!CanEnter(new Cell(x - stepX, y - stepY), new Cell(x, y), canOccupy, canCross))
                    return false;
            }

            return true;
        }

        private static bool CanEnter(
            Cell from,
            Cell to,
            Func<Cell, bool> canOccupy,
            Func<Cell, Cell, bool> canCross)
        {
            return canOccupy(to) && (canCross == null || canCross(from, to));
        }
    }
}
