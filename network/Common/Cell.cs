using MessagePack;

namespace network.common
{
    [MessagePackObject]
    public class Cell(int x, int y) : IMessagePackObject, IEquatable<Cell>
    {
        [Key("x")]
        public int X { get; set; } = x;

        [Key("y")]
        public int Y { get; set; } = y;

        public override bool Equals(object? obj) => Equals(obj as Cell);

        public bool Equals(Cell? other)
        {
            return other != null && X == other.X && Y == other.Y;
        }

        public override int GetHashCode() => HashCode.Combine(X, Y);

        public static Cell Clone(Cell cell) => new(cell.X, cell.Y);
        public Cell Clone() => new(X, Y);

        public static bool operator ==(Cell? left, Cell? right)
        {
            return EqualityComparer<Cell>.Default.Equals(left, right);
        }

        public static bool operator !=(Cell? left, Cell? right)
        {
            return !(left == right);
        }
    }
}
