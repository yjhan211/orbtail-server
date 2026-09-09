using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.items;

/// <summary>
/// 서버가 승인한 이동 구간에서 만난 아이템을 다음 틱까지 기억한다.
/// 구간마다 후보를 찾으므로 꺾인 경로를 직선으로 잇거나 나중에 생성된 아이템을 소급해서 줍지 않는다.
/// 접근은 해당 매치 잠금 안에서만 한다.
/// </summary>
internal sealed class GroundItemPickupCandidates
{
    private const int MaximumCandidates = 1024;
    private readonly Dictionary<long, Candidate> _items = [];

    public void Record(GroundItemManager items, long playerId, AreaType area, Vector3f from, Vector3f to,
        MapId? transitionMap = null)
    {
        if (area == AreaType.None) return;
        foreach (var item in items.GetSnapshot(area))
        {
            // 공중에 있을 때 지나친 경로를 착지 후 획득에 재사용하지 않는다.
            if (items.IsLanding(item.GroundItemUid)) continue;
            // 자신이 떨어뜨린 아이템은 한 번 벗어나 차단이 해제되기 전까지 후보로 잡지 않는다.
            if (item.SourcePlayerId == playerId && playerId != 0) continue;
            var position = ClosestPoint(from, to, item.PositionX, item.PositionY);
            if (transitionMap is { } map &&
                GameMapData.GetCurrentArea(map, MapCoordinateConverter.WorldToCell(map, position)) != area)
                continue;
            float radius = item.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID
                ? GroundItemManager.SummonStonePickupRadius : GroundItemManager.PickupRadius;
            float dx = item.PositionX - position.X;
            float dy = item.PositionY - position.Y;
            if (dx * dx + dy * dy > radius * radius) continue;
            if (_items.Count >= MaximumCandidates && !_items.ContainsKey(item.GroundItemUid)) continue;
            _items.TryAdd(item.GroundItemUid, new(item.GroundItemUid, area, position));
        }
    }

    public Candidate[] Take()
    {
        var candidates = _items.Values.ToArray();
        _items.Clear();
        return candidates;
    }

    public void Clear() => _items.Clear();

    private static Vector3f ClosestPoint(Vector3f from, Vector3f to, float x, float y)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float lengthSquared = dx * dx + dy * dy;
        float t = lengthSquared > 0 ? Math.Clamp(((x - from.X) * dx + (y - from.Y) * dy) / lengthSquared, 0f, 1f) : 0f;
        return new Vector3f(from.X + dx * t, from.Y + dy * t, 0);
    }

    internal sealed record Candidate(long GroundItemUid, AreaType Area, Vector3f Position);
}
