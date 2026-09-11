using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

public enum GroundItemClaimStatus
{
    Success,
    NotFound,
    AreaMismatch,
    TooFar,
    Rejected,
    SourceBlocked,
    Landing
}

public enum GroundItemSpawnLayout
{
    Default,
    EliminationScatter
}

public enum GroundItemDisposition
{
    Store,
    AutoUse,
    LeaveOnGround
}

public sealed class MatchGroundItemState(TimeProvider? timeProvider = null)
{
    public const float PickupRadius = Config.GROUND_ITEM_PICKUP_RADIUS;
    public const float SummonStonePickupRadius = Config.SUMMON_STONE_PICKUP_RADIUS;
    public const int BandageItemId = 201000008;
    public const int FirstAidKitItemId = 201000018;
    public const int BandageRecovery = 15;
    public const int FirstAidKitRecovery = 35;

    public const int HeartItemId = Config.HEART_GROUND_ITEM_ID;
    public const int HeartRecovery = 105;

    private static bool IsImmediateUseItem(int itemId) => itemId is BandageItemId or HeartItemId;
    public static bool ShouldDropOnElimination(int itemId) => !IsImmediateUseItem(itemId);

    public static GroundItemDisposition ResolveDisposition(int itemId, int health, out int healthRecovery)
    {
        if (itemId == Config.JAM_GROUND_ITEM_ID)
        {
            healthRecovery = 0;
            return GroundItemDisposition.LeaveOnGround;
        }
        healthRecovery = itemId switch
        {
            BandageItemId => BandageRecovery,
            FirstAidKitItemId => FirstAidKitRecovery,
            HeartItemId => HeartRecovery,
            _ => 0
        };

        if (itemId == HeartItemId && health >= Config.MAX_HEALTH)
        {
            return GroundItemDisposition.LeaveOnGround;
        }

        if (healthRecovery == 0)
        {
            return GroundItemDisposition.Store;
        }

        return IsImmediateUseItem(itemId) ? GroundItemDisposition.AutoUse : GroundItemDisposition.Store;
    }

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<long, GroundItemInfo> _items = new();
    private readonly Dictionary<long, DateTimeOffset> _spawnedAtUtc = new();
    private long _sequence;
    private bool _released;

    internal void Release()
    {
        _released = true;
        _items.Clear();
        _spawnedAtUtc.Clear();
    }

    public List<GroundItemInfo> SpawnItems(AreaType area, float originX, float originY, IReadOnlyList<int> itemIds, long sourcePlayerId = 0, MapId? mapId = null, GroundItemSpawnLayout layout = GroundItemSpawnLayout.Default)
    {
        if (_released)
        {
            throw new InvalidOperationException("Match is not available.");
        }
        if (area == AreaType.None || itemIds.Count == 0)
        {
            return [];
        }

        mapId ??= Config.SWARM_MATCH_MAP;
        var spawned = new List<GroundItemInfo>(itemIds.Count);
        for (int i = 0; i < itemIds.Count; i++)
        {
            var landing = ResolveLandingPosition(mapId.Value, area, originX, originY, i, itemIds.Count, layout);
            var item = new GroundItemInfo
            {
                GroundItemUid = ++_sequence,
                ItemId = itemIds[i],
                AreaType = (int)area,
                PositionX = landing.X,
                PositionY = landing.Y,
                SpawnOriginX = originX,
                SpawnOriginY = originY,
                SourcePlayerId = sourcePlayerId
            };
            _items[item.GroundItemUid] = item;
            _spawnedAtUtc[item.GroundItemUid] = _timeProvider.GetUtcNow();
            spawned.Add(Clone(item));
        }
        return spawned;
    }

    public List<GroundItemInfo> GetSnapshot(AreaType area)
    {
        return _items.Values.Where(item => item.AreaType == (int)area).OrderBy(item => item.GroundItemUid).Select(Clone).ToList();
    }

    public GroundItemInfo? GetItem(long groundItemUid)
    {
        return _items.TryGetValue(groundItemUid, out var item) ? Clone(item) : null;
    }

    public bool IsYoungerThan(long groundItemUid, TimeSpan age)
    {
        return _spawnedAtUtc.TryGetValue(groundItemUid, out var spawnedAt) && _timeProvider.GetUtcNow() - spawnedAt < age;
    }

    public bool IsLanding(long groundItemUid)
    {
        return _items.TryGetValue(groundItemUid, out var item) && IsLanding(item);
    }

    private bool IsLanding(GroundItemInfo item)
    {
        if (!_spawnedAtUtc.TryGetValue(item.GroundItemUid, out var spawnedAt))
        {
            return false;
        }
        float dx = item.PositionX - item.SpawnOriginX;
        float dy = item.PositionY - item.SpawnOriginY;
        float duration = Config.GetGroundItemLandingSeconds(MathF.Sqrt(dx * dx + dy * dy));
        return _timeProvider.GetUtcNow() - spawnedAt < TimeSpan.FromSeconds(duration);
    }

    public GroundItemClaimStatus TryClaim(long groundItemUid, long claimingPlayerId, AreaType playerArea, float playerX, float playerY, Func<GroundItemInfo, bool> accept, out GroundItemInfo? claimedItem)
    {
        claimedItem = null;
        if (!_items.TryGetValue(groundItemUid, out var item))
        {
            return GroundItemClaimStatus.NotFound;
        }
        if (IsLanding(item))
        {
            return GroundItemClaimStatus.Landing;
        }
        if (item.SourcePlayerId != 0 && item.SourcePlayerId == claimingPlayerId)
        {
            return GroundItemClaimStatus.SourceBlocked;
        }
        if (item.AreaType != (int)playerArea)
        {
            return GroundItemClaimStatus.AreaMismatch;
        }

        float dx = item.PositionX - playerX;
        float dy = item.PositionY - playerY;
        float pickupRadius = item.ItemId is Config.SUMMON_STONE_GROUND_ITEM_ID ? SummonStonePickupRadius : PickupRadius;
        if (dx * dx + dy * dy > pickupRadius * pickupRadius)
        {
            return GroundItemClaimStatus.TooFar;
        }
        if (!accept(item))
        {
            return GroundItemClaimStatus.Rejected;
        }

        _items.Remove(groundItemUid);
        _spawnedAtUtc.Remove(groundItemUid);
        claimedItem = Clone(item);
        return GroundItemClaimStatus.Success;
    }

    public void ReleaseSourcePickupBlocks(long playerId, AreaType area, float playerX, float playerY)
    {
        const float releaseRadius = 1.4f;
        foreach (var item in _items.Values)
        {
            if (item.SourcePlayerId != playerId)
            {
                continue;
            }
            float dx = item.PositionX - playerX;
            float dy = item.PositionY - playerY;
            if (item.AreaType != (int)area || dx * dx + dy * dy > releaseRadius * releaseRadius)
            {
                item.SourcePlayerId = 0;
            }
        }
    }

    private static (float X, float Y) ResolveLandingPosition(MapId mapId, AreaType area, float originX, float originY, int itemIndex, int itemCount, GroundItemSpawnLayout layout)
    {
        if (layout == GroundItemSpawnLayout.EliminationScatter)
        {
            return ResolveEliminationScatterLanding(mapId, area, originX, originY, itemIndex, itemCount);
        }

        var originCell = WorldPositionToCell(mapId, originX, originY);
        var areaRegions = GameMapData.GetAreas(mapId).Where(candidate => candidate.AreaType == area).ToList();
        var region = areaRegions.FirstOrDefault(candidate => candidate.Contains(originCell)) ??
                     areaRegions.OrderByDescending(candidate =>
                         (candidate.End.X - candidate.Start.X + 1) *
                         (candidate.End.Y - candidate.Start.Y + 1))
                         .FirstOrDefault();
        if (region == null)
        {
            return ResolveFallbackLanding(originX, originY, itemIndex, itemCount);
        }

        var centerCell = new Cell((region.Start.X + region.End.X) / 2, (region.Start.Y + region.End.Y) / 2);
        var centerWorld = CellToWorldPosition(mapId, centerCell);
        float verticalDirection = centerWorld.Y >= originY ? 1f : -1f;
        float groupOffset = (itemIndex - (itemCount - 1) * 0.5f) * 0.24f;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            float distance = MathF.Max(0.3f, 0.85f - attempt * 0.05f);
            float scatterX = groupOffset + (Random.Shared.NextSingle() - 0.5f) * 0.22f;
            float candidateX = originX + scatterX;
            float candidateY = originY + verticalDirection * distance;
            var candidateCell = WorldPositionToCell(mapId, candidateX, candidateY);
            if (GameMapData.GetCurrentArea(mapId, candidateCell) != area || !GameMapData.IsMoveablePosition(mapId, candidateCell))
            {
                continue;
            }

            return (candidateX, candidateY);
        }

        return ResolveFallbackLanding(originX, originY, itemIndex, itemCount);
    }

    private static (float X, float Y) ResolveEliminationScatterLanding(MapId mapId, AreaType area, float originX, float originY, int itemIndex, int itemCount)
    {
        const float minRadius = PickupRadius * 2.1f;
        const float maxRadius = 3.45f;
        const float goldenAngle = 2.3999632f;
        float baseAngle = itemCount <= 1 ? Random.Shared.NextSingle() * MathF.Tau : MathF.Tau * itemIndex / itemCount + (Random.Shared.NextSingle() - 0.5f) * 0.22f;
        for (int attempt = 0; attempt < 24; attempt++)
        {
            float radius = MathF.Max(minRadius, maxRadius - (attempt / 6) * 0.25f);
            float angle = baseAngle + attempt * goldenAngle;
            float candidateX = originX + MathF.Cos(angle) * radius;
            float candidateY = originY + MathF.Sin(angle) * radius;
            var candidateCell = WorldPositionToCell(mapId, candidateX, candidateY);
            if (GameMapData.GetCurrentArea(mapId, candidateCell) != area || !GameMapData.IsMoveablePosition(mapId, candidateCell))
            {
                continue;
            }

            return (candidateX, candidateY);
        }

        return ResolveFallbackLanding(originX, originY, itemIndex, itemCount);
    }

    private static Cell WorldPositionToCell(MapId mapId, float worldX, float worldY) => MapCoordinateConverter.WorldToCell(mapId, new Vector3f(worldX, worldY, 0f));

    private static (float X, float Y) ResolveFallbackLanding(float originX, float originY, int itemIndex, int itemCount)
    {
        float angle = itemCount == 1 ? 0f : MathF.Tau * itemIndex / itemCount;
        float radius = itemCount == 1 ? 0.25f : 0.42f;
        return (originX + MathF.Cos(angle) * radius, originY + MathF.Sin(angle) * radius * 0.55f);
    }

    private static (float X, float Y) CellToWorldPosition(MapId mapId, Cell cell) => MapCoordinateConverter.CellToWorldPoint(mapId, cell);

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
