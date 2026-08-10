using System.Collections.Concurrent;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

public enum GroundItemClaimStatus
{
    Success,
    NotFound,
    AreaMismatch,
    TooFar,
    Rejected,
    SourceBlocked,
    Reserved
}

public enum GroundItemSpawnLayout
{
    Default,
    EliminationScatter
}

public sealed class GroundItemManager
{
    public const float PickupRadius = 1.15f;

    // 소환석 자석 흡수 (#219): 접촉이 아니라 근처를 지나가면 딸려온다 — SB 코인 흡수 문법.
    public const float SummonStonePickupRadius = 3.5f;
    public static readonly TimeSpan DiscovererPickupWindow = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<long, MatchingGroundItemState> _matchingStates = new();
    private readonly TimeProvider _timeProvider;

    public GroundItemManager(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void InitializeMatching(long matchingId) =>
        _matchingStates.GetOrAdd(matchingId, _ => new MatchingGroundItemState(matchingId));

    public List<GroundItemInfo> SpawnItems(long matchingId, AreaType area, float originX, float originY,
        IReadOnlyList<int> itemIds, long sourcePlayerId = 0, MapId mapId = MapId.School,
        long discovererPlayerId = 0, TimeSpan? discovererPickupWindow = null,
        GroundItemSpawnLayout layout = GroundItemSpawnLayout.Default)
    {
        if (matchingId <= 0 || area == AreaType.None || itemIds.Count == 0)
            return new List<GroundItemInfo>();

        var state = _matchingStates.GetOrAdd(matchingId, id => new MatchingGroundItemState(id));
        lock (state.SyncRoot)
        {
            var spawned = new List<GroundItemInfo>(itemIds.Count);
            for (int i = 0; i < itemIds.Count; i++)
            {
                var landing = ResolveLandingPosition(mapId, area, originX, originY, i, itemIds.Count, layout);
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

    public List<GroundItemInfo> GetSnapshot(long matchingId, AreaType area)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var state))
            return new List<GroundItemInfo>();

        lock (state.SyncRoot)
            return state.Items.Values
                .Where(item => item.AreaType == (int)area)
                .OrderBy(item => item.GroundItemUid)
                .Select(Clone)
                .ToList();
    }

    public bool Exists(long matchingId, long groundItemUid)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var state))
            return false;
        lock (state.SyncRoot)
            return state.Items.ContainsKey(groundItemUid);
    }

    /// <summary>
    ///     스폰 후 경과가 age 미만인지 — 봇 줍기 반응 지연(#222)용. 봇은 이 창이 지나야
    ///     소환석에 반응한다. 사람의 눈·조작 시간을 흉내 내 낙수 선점권을 사람에게 준다.
    /// </summary>
    public bool IsYoungerThan(long matchingId, long groundItemUid, TimeSpan age)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var state)) return false;
        lock (state.SyncRoot)
            return state.SpawnedAtUtc.TryGetValue(groundItemUid, out var spawnedAt) &&
                   _timeProvider.GetUtcNow() - spawnedAt < age;
    }

    public GroundItemInfo? GetItem(long matchingId, long groundItemUid)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var state))
            return null;
        lock (state.SyncRoot)
            return state.Items.TryGetValue(groundItemUid, out var item) ? Clone(item) : null;
    }
    public long GetDiscovererPlayerId(long matchingId, long groundItemUid)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var state)) return 0;
        lock (state.SyncRoot)
            return state.DiscovererPlayerIds.GetValueOrDefault(groundItemUid);
    }

    public List<ExpiredGroundItemReservation> ExpireClaimReservations(long matchingId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var state))
            return [];

        var now = _timeProvider.GetUtcNow();
        lock (state.SyncRoot)
        {
            var expired = state.ClaimReservations
                .Where(entry => entry.Value.ExpiresAtUtc <= now)
                .Select(entry => new ExpiredGroundItemReservation(
                    entry.Key,
                    state.Items.TryGetValue(entry.Key, out var item) ? item.ItemId : 0,
                    entry.Value.PlayerId,
                    entry.Value.ExpiresAtUtc.ToUnixTimeMilliseconds()))
                .ToList();
            foreach (var reservation in expired)
                state.ClaimReservations.Remove(reservation.GroundItemUid);
            return expired;
        }
    }

    public GroundItemClaimStatus TryClaim(long matchingId, long groundItemUid, long claimingPlayerId, AreaType playerArea,
        float playerX, float playerY, Func<GroundItemInfo, bool> accept,
        out GroundItemInfo? claimedItem)
    {
        claimedItem = null;
        if (!_matchingStates.TryGetValue(matchingId, out var state))
            return GroundItemClaimStatus.NotFound;

        lock (state.SyncRoot)
        {
            if (!state.Items.TryGetValue(groundItemUid, out var item))
                return GroundItemClaimStatus.NotFound;
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
            float pickupRadius = item.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID
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

    public void ReleaseSourcePickupBlocks(long matchingId, long playerId, AreaType area, float playerX, float playerY)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var state)) return;
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

    public void ReleaseClaimReservationsForPlayer(long matchingId, long playerId)
    {
        if (playerId == 0 || !_matchingStates.TryGetValue(matchingId, out var state)) return;

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

    public void RemoveMatchingState(long matchingId) => _matchingStates.TryRemove(matchingId, out _);

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

    private sealed class MatchingGroundItemState(long matchingId)
    {
        private long _sequence;
        public object SyncRoot { get; } = new();
        public Dictionary<long, GroundItemInfo> Items { get; } = new();
        public Dictionary<long, GroundItemClaimReservation> ClaimReservations { get; } = new();
        public Dictionary<long, long> DiscovererPlayerIds { get; } = new();
        public Dictionary<long, DateTimeOffset> SpawnedAtUtc { get; } = new();
        public long NextUid() => checked(matchingId * 1_000_000L + ++_sequence);
    }

    public readonly record struct ExpiredGroundItemReservation(
        long GroundItemUid,
        int ItemId,
        long DiscovererPlayerId,
        long ExpiresAtUnixMs);

    private readonly record struct GroundItemClaimReservation(long PlayerId, DateTimeOffset ExpiresAtUtc);
}
