namespace network.common
{
    public class ChangeMapInfo
    {
        public MapID MapId { get; set; }
        public long MapSubId { get; set; }
        public Cell SpawnCell { get; set; }
        public bool IsFlip { get; set; }

        public ChangeMapInfo(MapID mapId, long mapSubId, Cell spawnCell, bool isFlip)
        {
            MapId = mapId;
            MapSubId = mapSubId;
            SpawnCell = spawnCell;
            IsFlip = isFlip;
        }
    }
}
