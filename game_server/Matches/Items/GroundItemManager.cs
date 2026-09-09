using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.items;

public enum GroundItemClaimStatus
{
    Success,
    NotFound,
    AreaMismatch,
    TooFar,
    Rejected,
    SourceBlocked,
    Reserved,
    Landing
}

public enum GroundItemSpawnLayout
{
    Default,
    EliminationScatter
}

/// <summary>매치 런타임의 바닥 아이템에 생성·조회·획득 규칙을 적용한다. 매치별 사전은 소유하지 않는다.</summary>
public sealed class GroundItemManager
{
    public const float PickupRadius = Config.GROUND_ITEM_PICKUP_RADIUS;

    // 소환석 자석 흡수 (#219): 접촉이 아니라 근처를 지나가면 딸려온다 — SB 코인 흡수 문법.
    public const float SummonStonePickupRadius = Config.SUMMON_STONE_PICKUP_RADIUS;

    private readonly long _matchingId;
    private MatchingGroundItemState? _state;
    private readonly TimeProvider _timeProvider;

    internal GroundItemManager(long matchingId, TimeProvider? timeProvider = null)
    {
        _matchingId = matchingId;
        _state = new MatchingGroundItemState(matchingId);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal void Release() => Interlocked.Exchange(ref _state, null);

    public List<GroundItemInfo> SpawnItems(AreaType area, float originX, float originY,
        IReadOnlyList<int> itemIds, long sourcePlayerId = 0, MapId? mapId = null,
        TimeSpan? lifetime = null,
        long discovererPlayerId = 0, TimeSpan? discovererPickupWindow = null,
        GroundItemSpawnLayout layout = GroundItemSpawnLayout.Default)
    {
        mapId ??= Config.SWARM_MATCH_MAP;
        if (_matchingId <= 0 || area == AreaType.None || itemIds.Count == 0)
            return new List<GroundItemInfo>();

        var state = GetRequiredState();
        lock (state.SyncRoot)
        {
            var spawned = new List<GroundItemInfo>(itemIds.Count);
            for (int i = 0; i < itemIds.Count; i++)
            {
                var landing = ResolveLandingPosition(mapId.Value, area, originX, originY, i, itemIds.Count, layout);
                var item = new GroundItemInfo
                {
                    GroundItemUid = state.NextUid(),
                    ItemId = itemIds[i],
                    AreaType = (int)area,
                    PositionX = landing.X,
                    PositionY = landing.Y,
                    SpawnOriginX = originX,
                    SpawnOriginY = originY,
                    SourcePlayerId = sourcePlayerId
                };
                state.Items[item.GroundItemUid] = item;
                state.SpawnedAtUtc[item.GroundItemUid] = _timeProvider.GetUtcNow();
                if (lifetime is { } span && span > TimeSpan.Zero)
                {
                    var expiries = state.ItemExpiries;
                    expiries[item.GroundItemUid] = _timeProvider.GetUtcNow() + span;
                }
                if (discovererPlayerId != 0)
                    state.DiscovererPlayerIds[item.GroundItemUid] = discovererPlayerId;
                if (discovererPlayerId != 0 &&
                    discovererPickupWindow is { } pickupWindow &&
                    pickupWindow > TimeSpan.Zero)
                {
                    state.ClaimReservations[item.GroundItemUid] = new GroundItemClaimReservation(
                        discovererPlayerId, _timeProvider.GetUtcNow().Add(pickupWindow));
                }
                spawned.Add(Clone(item));
            }
            return spawned;
        }
    }

    public List<GroundItemInfo> GetSnapshot(AreaType area)
    {
        if (Volatile.Read(ref _state) is not { } state)
            return new List<GroundItemInfo>();

        lock (state.SyncRoot)
            return state.Items.Values
                .Where(item => item.AreaType == (int)area)
                .OrderBy(item => item.GroundItemUid)
                .Select(Clone)
                .ToList();
    }

    /// <summary>
    ///     스폰 후 경과가 age 미만인지 — 봇 줍기 반응 지연(#222)용. 봇은 이 창이 지나야
    ///     소환석에 반응한다. 사람의 눈·조작 시간을 흉내 내 낙수 선점권을 사람에게 준다.
    /// </summary>
    public bool IsYoungerThan(long groundItemUid, TimeSpan age)
    {
        if (Volatile.Read(ref _state) is not { } state) return false;
        lock (state.SyncRoot)
            return state.SpawnedAtUtc.TryGetValue(groundItemUid, out var spawnedAt) &&
                   _timeProvider.GetUtcNow() - spawnedAt < age;
    }

    public bool IsLanding(long groundItemUid)
    {
        if (Volatile.Read(ref _state) is not { } state) return false;
        lock (state.SyncRoot)
            return state.Items.TryGetValue(groundItemUid, out var item) && IsLanding(state, item);
    }

    private bool IsLanding(MatchingGroundItemState state, GroundItemInfo item)
    {
        if (!state.SpawnedAtUtc.TryGetValue(item.GroundItemUid, out var spawnedAt)) return false;
        float dx = item.PositionX - item.SpawnOriginX;
        float dy = item.PositionY - item.SpawnOriginY;
        float duration = Config.GetGroundItemLandingSeconds(MathF.Sqrt(dx * dx + dy * dy));
        return _timeProvider.GetUtcNow() - spawnedAt < TimeSpan.FromSeconds(duration);
    }

    public GroundItemInfo? GetItem(long groundItemUid)
    {
        if (Volatile.Read(ref _state) is not { } state)
            return null;
        lock (state.SyncRoot)
            return state.Items.TryGetValue(groundItemUid, out var item) ? Clone(item) : null;
    }
    public long GetDiscovererPlayerId(long groundItemUid)
    {
        if (Volatile.Read(ref _state) is not { } state) return 0;
        lock (state.SyncRoot)
            return state.DiscovererPlayerIds.GetValueOrDefault(groundItemUid);
    }

    public GroundItemClaimStatus TryClaim(long groundItemUid, long claimingPlayerId, AreaType playerArea,
        float playerX, float playerY, Func<GroundItemInfo, bool> accept,
        out GroundItemInfo? claimedItem)
    {
        claimedItem = null;
        if (Volatile.Read(ref _state) is not { } state)
            return GroundItemClaimStatus.NotFound;

        lock (state.SyncRoot)
        {
            if (!state.Items.TryGetValue(groundItemUid, out var item))
                return GroundItemClaimStatus.NotFound;
            if (IsLanding(state, item))
                return GroundItemClaimStatus.Landing;
            if (state.ClaimReservations.TryGetValue(groundItemUid, out var reservation))
            {
                if (reservation.ExpiresAtUtc <= _timeProvider.GetUtcNow())
                {
                    state.ClaimReservations.Remove(groundItemUid);
                }
                else if (reservation.PlayerId != claimingPlayerId)
                {
                    return GroundItemClaimStatus.Reserved;
                }
            }
            if (item.SourcePlayerId != 0 && item.SourcePlayerId == claimingPlayerId)
                return GroundItemClaimStatus.SourceBlocked;
            if (item.AreaType != (int)playerArea)
                return GroundItemClaimStatus.AreaMismatch;

            float dx = item.PositionX - playerX;
            float dy = item.PositionY - playerY;
            // 소환석에는 일반 아이템보다 넓은 획득 반경을 적용한다.
            float pickupRadius = item.ItemId is Config.SUMMON_STONE_GROUND_ITEM_ID
                ? SummonStonePickupRadius
                : PickupRadius;
            if (dx * dx + dy * dy > pickupRadius * pickupRadius)
                return GroundItemClaimStatus.TooFar;
            if (!accept(item))
                return GroundItemClaimStatus.Rejected;

            state.Items.Remove(groundItemUid);
            state.DiscovererPlayerIds.Remove(groundItemUid);
            state.ClaimReservations.Remove(groundItemUid);
            state.SpawnedAtUtc.Remove(groundItemUid);
            claimedItem = Clone(item);
            return GroundItemClaimStatus.Success;
        }
    }

    public void ReleaseSourcePickupBlocks(long playerId, AreaType area, float playerX, float playerY)
    {
        if (Volatile.Read(ref _state) is not { } state) return;
        const float releaseRadius = 1.4f;
        lock (state.SyncRoot)
        {
            foreach (var item in state.Items.Values)
            {
                if (item.SourcePlayerId != playerId) continue;
                float dx = item.PositionX - playerX;
                float dy = item.PositionY - playerY;
                if (item.AreaType != (int)area || dx * dx + dy * dy > releaseRadius * releaseRadius)
                    item.SourcePlayerId = 0;
            }
        }
    }

    public void ReleaseClaimReservationsForPlayer(long playerId)
    {
        if (playerId == 0 || Volatile.Read(ref _state) is not { } state) return;

        lock (state.SyncRoot)
        {
            var reservedItemIds = state.ClaimReservations
                .Where(entry => entry.Value.PlayerId == playerId)
                .Select(entry => entry.Key)
                .ToArray();
            foreach (long groundItemUid in reservedItemIds)
                state.ClaimReservations.Remove(groundItemUid);
        }
    }

    private MatchingGroundItemState GetRequiredState() =>
        Volatile.Read(ref _state)
        ?? throw new InvalidOperationException($"Match is not available: {_matchingId}");

    private static (float X, float Y) ResolveLandingPosition(MapId mapId, AreaType area, float originX,
        float originY, int itemIndex, int itemCount, GroundItemSpawnLayout layout)
    {
        if (layout == GroundItemSpawnLayout.EliminationScatter)
            return ResolveEliminationScatterLanding(mapId, area, originX, originY, itemIndex, itemCount);

        var originCell = WorldPositionToCell(mapId, originX, originY);
        var areaRegions = GameMapData.GetAreas(mapId)
            .Where(candidate => candidate.AreaType == area)
            .ToList();
        var region = areaRegions.FirstOrDefault(candidate => candidate.Contains(originCell)) ??
                     areaRegions.OrderByDescending(candidate =>
                         (candidate.End.X - candidate.Start.X + 1) *
                         (candidate.End.Y - candidate.Start.Y + 1))
                         .FirstOrDefault();
        if (region == null)
            return ResolveFallbackLanding(originX, originY, itemIndex, itemCount);

        var centerCell = new Cell(
            (region.Start.X + region.End.X) / 2,
            (region.Start.Y + region.End.Y) / 2);
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
            if (GameMapData.GetCurrentArea(mapId, candidateCell) != area ||
                !GameMapData.IsMoveablePosition(mapId, candidateCell))
                continue;

            return (candidateX, candidateY);
        }

        return ResolveFallbackLanding(originX, originY, itemIndex, itemCount);
    }

    private static (float X, float Y) ResolveEliminationScatterLanding(MapId mapId, AreaType area,
        float originX, float originY, int itemIndex, int itemCount)
    {
        // Keep loot outside one contact-pickup circle so a player must choose which orb to approach.
        const float minRadius = PickupRadius * 2.1f;
        const float maxRadius = 3.45f;
        const float goldenAngle = 2.3999632f;
        float baseAngle = itemCount <= 1
            ? Random.Shared.NextSingle() * MathF.Tau
            : MathF.Tau * itemIndex / itemCount + (Random.Shared.NextSingle() - 0.5f) * 0.22f;

        for (int attempt = 0; attempt < 24; attempt++)
        {
            float radius = MathF.Max(minRadius, maxRadius - (attempt / 6) * 0.25f);
            float angle = baseAngle + attempt * goldenAngle;
            float candidateX = originX + MathF.Cos(angle) * radius;
            float candidateY = originY + MathF.Sin(angle) * radius;
            var candidateCell = WorldPositionToCell(mapId, candidateX, candidateY);
            if (GameMapData.GetCurrentArea(mapId, candidateCell) != area ||
                !GameMapData.IsMoveablePosition(mapId, candidateCell))
                continue;

            return (candidateX, candidateY);
        }

        // A narrow room can have no valid point at the target radius. Keep the item rather than lose it.
        return ResolveFallbackLanding(originX, originY, itemIndex, itemCount);
    }

    private static Cell WorldPositionToCell(MapId mapId, float worldX, float worldY) =>
        MapCoordinateConverter.WorldToCell(mapId, new Vector3f(worldX, worldY, 0f));

    private static (float X, float Y) ResolveFallbackLanding(float originX, float originY, int itemIndex,
        int itemCount)
    {
        float angle = itemCount == 1 ? 0f : MathF.Tau * itemIndex / itemCount;
        float radius = itemCount == 1 ? 0.25f : 0.42f;
        return (originX + MathF.Cos(angle) * radius, originY + MathF.Sin(angle) * radius * 0.55f);
    }

    private static (float X, float Y) CellToWorldPosition(MapId mapId, Cell cell) =>
        MapCoordinateConverter.CellToWorldPoint(mapId, cell);

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

    internal sealed class MatchingGroundItemState(long matchingId)
    {
        private long _sequence;
        public object SyncRoot { get; } = new();
        public Dictionary<long, GroundItemInfo> Items { get; } = new();
        // 수명이 지정된 아이템의 만료 시각도 해당 매치 데이터에 함께 보관한다.
        public Dictionary<long, DateTimeOffset> ItemExpiries { get; } = new();
        public Dictionary<long, GroundItemClaimReservation> ClaimReservations { get; } = new();
        public Dictionary<long, long> DiscovererPlayerIds { get; } = new();
        public Dictionary<long, DateTimeOffset> SpawnedAtUtc { get; } = new();
        public long NextUid() => checked(matchingId * 1_000_000L + ++_sequence);
    }

    internal readonly record struct GroundItemClaimReservation(long PlayerId, DateTimeOffset ExpiresAtUtc);
}
