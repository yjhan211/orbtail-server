// ReSharper disable All
namespace network.common.data.models
{
    public class ChangeMapInfo
    {
        public ChangeMapInfo(MapId mapId, long mapSubId, Cell spawnCell, bool isFlip)
        {
            MapId = mapId;
            MapSubId = mapSubId;
            SpawnCell = spawnCell;
            IsFlip = isFlip;
        }

        public MapId MapId { get; set; }
        public long MapSubId { get; set; }
        public Cell SpawnCell { get; set; }
        public bool IsFlip { get; set; }
    }
}