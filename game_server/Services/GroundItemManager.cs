using System.Collections.Concurrent;
using network.common;
using network.common.data.models;

namespace game_server.services;

public enum GroundItemClaimStatus
{
    Success,
    NotFound,
    AreaMismatch,
    TooFar,
    Rejected,
    SourceBlocked
}

public sealed class GroundItemManager
{
    public const float PickupRadius = 1.15f;
    private readonly ConcurrentDictionary<long, MatchingGroundItemState> _matchingStates = new();

    public void InitializeMatching(long matchingId) =>
        _matchingStates.GetOrAdd(matchingId, _ => new MatchingGroundItemState(matchingId));

    public List<GroundItemInfo> SpawnItems(long matchingId, AreaType area, float originX, float originY,
        IReadOnlyList<int> itemIds, long sourcePlayerId = 0)
    {
        if (matchingId <= 0 || area == AreaType.None || itemIds.Count == 0)
            return new List<GroundItemInfo>();

        var state = _matchingStates.GetOrAdd(matchingId, id => new MatchingGroundItemState(id));
        lock (state.SyncRoot)
        {
            var spawned = new List<GroundItemInfo>(itemIds.Count);
            for (int i = 0; i < itemIds.Count; i++)
            {
                float angle = itemIds.Count == 1 ? 0f : MathF.Tau * i / itemIds.Count;
                float radius = itemIds.Count == 1 ? 0.25f : 0.42f;
                var item = new GroundItemInfo
                {
                    GroundItemUid = state.NextUid(),
                    ItemId = itemIds[i],
                    AreaType = (int)area,
                    PositionX = originX + MathF.Cos(angle) * radius,
                    PositionY = originY + MathF.Sin(angle) * radius * 0.55f,
                    SourcePlayerId = sourcePlayerId
                };
                state.Items[item.GroundItemUid] = item;
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
            if (item.SourcePlayerId != 0 && item.SourcePlayerId == claimingPlayerId)
                return GroundItemClaimStatus.SourceBlocked;
            if (item.AreaType != (int)playerArea)
                return GroundItemClaimStatus.AreaMismatch;

            float dx = item.PositionX - playerX;
            float dy = item.PositionY - playerY;
            if (dx * dx + dy * dy > PickupRadius * PickupRadius)
                return GroundItemClaimStatus.TooFar;
            if (!accept(item))
                return GroundItemClaimStatus.Rejected;

            state.Items.Remove(groundItemUid);
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
    public void RemoveMatchingState(long matchingId) => _matchingStates.TryRemove(matchingId, out _);

    private static GroundItemInfo Clone(GroundItemInfo source) => new()
    {
        GroundItemUid = source.GroundItemUid,
        ItemId = source.ItemId,
        AreaType = source.AreaType,
        PositionX = source.PositionX,
        PositionY = source.PositionY,
        SourcePlayerId = source.SourcePlayerId
    };

    private sealed class MatchingGroundItemState(long matchingId)
    {
        private long _sequence;
        public object SyncRoot { get; } = new();
        public Dictionary<long, GroundItemInfo> Items { get; } = new();
        public long NextUid() => checked(matchingId * 1_000_000L + ++_sequence);
    }
}
