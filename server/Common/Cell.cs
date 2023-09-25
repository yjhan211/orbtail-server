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

        public bool Equals(Cell target)
        {
            return this.x == target.x && this.y == target.y;
        }

        public static Cell Clone(Cell cell)
        {
            return new Cell(cell.x, cell.y);
        }
    }
}
