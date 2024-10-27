namespace network.common.models;

public class ChangeMapInfo(MapId mapId, long mapSubId, Cell spawnCell, bool isFlip)
{
    public MapId MapId { get; set; } = mapId;
    public long MapSubId { get; set; } = mapSubId;
    public Cell SpawnCell { get; set; } = spawnCell;
    public bool IsFlip { get; set; } = isFlip;
}