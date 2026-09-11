using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     매치별 바닥 아이템과 생성 시각을 관리한다.
///     아이템의 낙하 위치와 착지 여부를 계산하고, 조회 시 복사본을 반환한다.
///     MatchRuntime이 소유하며, 호출자는 매치 잠금을 보유해야 한다.
/// </summary>
public sealed class MatchGroundItemState(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<long, GroundItemInfo> _items = new();
    private readonly Dictionary<long, DateTimeOffset> _spawnedAtUtc = new();
    private long _sequence;

    internal void Release()
    {
        _items.Clear();
        _spawnedAtUtc.Clear();
    }

    public List<GroundItemInfo> SpawnItems(AreaType area, float originX, float originY, IReadOnlyList<int> itemIds)
    {
        if (area == AreaType.None || itemIds.Count == 0)
        {
            return [];
        }

        var spawned = new List<GroundItemInfo>(itemIds.Count);
        for (int i = 0; i < itemIds.Count; i++)
        {
            var landing = ChooseLandingPosition(area, originX, originY, i, itemIds.Count);
            var item = new GroundItemInfo
            {
                GroundItemUid = ++_sequence,
                ItemId = itemIds[i],
                AreaType = (int)area,
                PositionX = landing.X,
                PositionY = landing.Y,
                SpawnOriginX = originX,
                SpawnOriginY = originY
            };
            _items[item.GroundItemUid] = item;
            _spawnedAtUtc[item.GroundItemUid] = _timeProvider.GetUtcNow();
            spawned.Add(Clone(item));
        }
        return spawned;
    }

    public List<GroundItemInfo> GetItemsInArea(AreaType area)
    {
        return _items.Values.Where(item => item.AreaType == (int)area).OrderBy(item => item.GroundItemUid).Select(Clone).ToList();
    }

    public GroundItemInfo? GetItem(long groundItemUid)
    {
        return _items.TryGetValue(groundItemUid, out var item) ? Clone(item) : null;
    }

    public bool WasSpawnedWithin(long groundItemUid, TimeSpan age)
    {
        return _spawnedAtUtc.TryGetValue(groundItemUid, out var spawnedAt) && _timeProvider.GetUtcNow() - spawnedAt < age;
    }

    public bool IsLanding(long groundItemUid)
    {
        if (!_items.TryGetValue(groundItemUid, out var item) || !_spawnedAtUtc.TryGetValue(groundItemUid, out var spawnedAt))
        {
            return false;
        }
        float dx = item.PositionX - item.SpawnOriginX;
        float dy = item.PositionY - item.SpawnOriginY;
        float duration = Config.GetGroundItemLandingSeconds(MathF.Sqrt(dx * dx + dy * dy));
        return _timeProvider.GetUtcNow() - spawnedAt < TimeSpan.FromSeconds(duration);
    }

    public GroundItemInfo? TakeItem(long groundItemUid)
    {
        if (!_items.Remove(groundItemUid, out var item))
        {
            return null;
        }
        _spawnedAtUtc.Remove(groundItemUid);
        return Clone(item);
    }

    private static (float X, float Y) ChooseLandingPosition(AreaType area, float originX, float originY, int itemIndex, int itemCount)
    {
        var mapId = Config.SWARM_MATCH_MAP;
        const float minRadius = Config.GROUND_ITEM_PICKUP_RADIUS * 2.1f;
        const float maxRadius = 3.45f;
        const float goldenAngle = 2.3999632f;
        const int attemptsPerRing = 6;
        const int ringCount = 4;
        const float ringStepCells = 0.25f;
        float baseAngle = itemCount <= 1 ? Random.Shared.NextSingle() * MathF.Tau : MathF.Tau * itemIndex / itemCount + (Random.Shared.NextSingle() - 0.5f) * 0.22f;
        for (int attempt = 0; attempt < attemptsPerRing * ringCount; attempt++)
        {
            int ring = attempt / attemptsPerRing;
            float radius = MathF.Max(minRadius, maxRadius - ring * ringStepCells);
            float angle = baseAngle + attempt * goldenAngle;
            float candidateX = originX + MathF.Cos(angle) * radius;
            float candidateY = originY + MathF.Sin(angle) * radius;
            var candidateCell = MapCoordinateConverter.WorldToCell(mapId, new Vector3f(candidateX, candidateY, 0f));
            if (GameMapData.GetCurrentArea(mapId, candidateCell) != area || !GameMapData.IsMoveablePosition(mapId, candidateCell))
            {
                continue;
            }

            return (candidateX, candidateY);
        }

        float fallbackAngle = itemCount == 1 ? 0f : MathF.Tau * itemIndex / itemCount;
        float fallbackRadius = itemCount == 1 ? 0.25f : 0.42f;
        return (originX + MathF.Cos(fallbackAngle) * fallbackRadius, originY + MathF.Sin(fallbackAngle) * fallbackRadius * 0.55f);
    }

    private static GroundItemInfo Clone(GroundItemInfo source) => new()
    {
        GroundItemUid = source.GroundItemUid,
        ItemId = source.ItemId,
        AreaType = source.AreaType,
        PositionX = source.PositionX,
        PositionY = source.PositionY,
        SpawnOriginX = source.SpawnOriginX,
        SpawnOriginY = source.SpawnOriginY,
        SourcePlayerId = source.SourcePlayerId
    };
}
