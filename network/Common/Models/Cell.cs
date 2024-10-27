using System.Diagnostics.CodeAnalysis;
using MessagePack;

namespace network.common.models;

[MessagePackObject]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
[SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
public class Cell(int x, int y) : IMessagePackObject, IEquatable<Cell>
{
    [Key("x")] public int X { get; set; } = x;

    [Key("y")] public int Y { get; set; } = y;

    public bool Equals(Cell? other)
    {
        return other != null && X == other.X && Y == other.Y;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as Cell);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y);
    }

    public static Cell Clone(Cell cell)
    {
        return new Cell(cell.X, cell.Y);
    }

    public Cell Clone()
    {
        return new Cell(X, Y);
    }

    public static bool operator ==(Cell? left, Cell? right)
    {
        return EqualityComparer<Cell>.Default.Equals(left, right);
    }

    public static bool operator !=(Cell? left, Cell? right)
    {
        return !(left == right);
    }

    public int GetDistance(Cell targetCell)
    {
        var deltaX = Math.Abs(X - targetCell.X);
        var deltaY = Math.Abs(Y - targetCell.Y);

        return deltaX + deltaY;
    }

    public DirectionType GetDirection(Cell targetCell)
    {
        var deltaX = X - targetCell.X;
        var deltaY = Y - targetCell.Y;

        if (deltaX > 0 && deltaY == 0) return DirectionType.TOP_LEFT;
        if (deltaX < 0 && deltaY == 0) return DirectionType.TOP_RIGHT;
        if (deltaX == 0 && deltaY < 0) return DirectionType.BOTTOM_LEFT;
        if (deltaX == 0 && deltaY > 0) return DirectionType.BOTTOM_RIGHT;

        return DirectionType.NONE;
    }

    public Cell GetNextCell(DirectionType direction)
    {
        var clone = Clone(this);
        switch (direction)
        {
            case DirectionType.TOP_LEFT:
                clone.X += 1;
                break;

            case DirectionType.TOP_RIGHT:
                clone.X -= 1;
                break;

            case DirectionType.BOTTOM_LEFT:
                clone.Y -= 1;
                break;

            case DirectionType.BOTTOM_RIGHT:
                clone.Y += 1;
                break;
        }

        return clone;
    }

    public List<Cell> GetBoundCellList()
    {
        var result = new List<Cell>();

        var minX = X - 13;
        var maxX = X + 14;
        var minY = Y - 7;
        var maxY = Y - 9;

        var line = 0;
        for (var x = minX; x <= maxX; x++)
        {
            line += 1;

            if (line <= 1)
            {
                minY -= 1;
                maxY += 2;
            }
            else if (line <= 4)
            {
                minY -= 1;
                maxY += 1;
            }
            else if (line == 5)
            {
                minY -= 1;
                maxY += 1;
            }
            else if (line == 6)
            {
                minY -= 1;
                maxY += 1;
            }
            else if (line == 7)
            {
                maxY += 1;
            }
            else if (line == 8)
            {
                minY += 1;
                maxY += 1;
            }
            else if (line == 9)
            {
                minY += 1;
                maxY += 1;
            }
            else if (line <= 22)
            {
                minY += 1;
                maxY += 1;
            }
            else if (line == 23)
            {
                minY += 1;
            }
            else
            {
                minY += 1;
                maxY -= 1;
            }

            for (var y = minY; y <= maxY; y++)
            {
                var cell = new Cell(x, y);
                result.Add(cell);
            }
        }

        return result;
    }
}