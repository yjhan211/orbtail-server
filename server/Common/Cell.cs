namespace game_server
{
    using MessagePack;

    [MessagePackObject]
    public class Cell : IMessagePackObject
    {
        public Cell(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        [Key("x")]
        public int x { get; set; }

        [Key("y")]
        public int y { get; set; }

        public override bool Equals(object? obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            var other = (Cell)obj;
            return this.x == other.x && this.y == other.y;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(this.x, this.y);
        }

        public static Cell Clone(Cell cell)
        {
            return new Cell(cell.x, cell.y);
        }
    }
}
