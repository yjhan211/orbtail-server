using System.Collections.Concurrent;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
/// Owns the server-authoritative summon economy for each player in a match.
/// A failed grant leaves both currency and the deterministic result sequence unchanged.
/// </summary>
public sealed class SummonStoneManager
{
    public const int NormalMonsterReward = 1;
    public const int CoreMonsterReward = 3;

    private static readonly int[] SummonCosts = [2, 3, 4, 5, 6];
    private static readonly int[] SummonPool = [107000010, 107000020, 107000030, 107000040];
    private static readonly int[] OpeningAttackPool = SummonPool
        .Where(itemId => !SurvivorOrbData.IsRecoveryOrb(itemId))
        .ToArray();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, PlayerSummonState>> _matchingStates = new();

    public IReadOnlyList<int> PoolItemIds => SummonPool;
    // Players begin without an orb, but can pay the first two summon costs (2 + 3).
    public static int InitialSummonStoneCount => SummonCosts[0] + SummonCosts[1];

    public SummonStoneSnapshot EnsureStartingStones(long matchingId, long playerId)
    {
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
        {
            if (!state.HasStartingStones)
            {
                state.StoneCount = checked(state.StoneCount + InitialSummonStoneCount);
                state.HasStartingStones = true;
            }

            return CreateSnapshot(state);
        }
    }

    public SummonStoneSnapshot AddStones(long matchingId, long playerId, int amount)
    {
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
        {
            if (amount > 0)
                state.StoneCount = checked(state.StoneCount + amount);
            return CreateSnapshot(state);
        }
    }

    public SummonStoneSnapshot GetSnapshot(long matchingId, long playerId)
    {
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
            return CreateSnapshot(state);
    }

    public SummonOrbAttempt TrySummon(long matchingId, long playerId, Func<int, InGameItemInfo?> grantItem)
    {
        ArgumentNullException.ThrowIfNull(grantItem);
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
        {
            int cost = GetCost(state.SuccessfulSummonCount);
            if (state.StoneCount < cost)
                return SummonOrbAttempt.Failed(ErrorCode.INSUFFICIENT_CURRENCY, CreateSnapshot(state));

            int itemId = SelectOrbItemId(matchingId, playerId, state.SuccessfulSummonCount);
            InGameItemInfo? item = grantItem(itemId);
            if (item == null)
                return SummonOrbAttempt.Failed(ErrorCode.INVENTORY_FULL, CreateSnapshot(state));

            state.StoneCount -= cost;
            state.SuccessfulSummonCount++;
            return new SummonOrbAttempt(true, ErrorCode.SUCCESS, itemId, item, CreateSnapshot(state));
        }
    }

    public void RemoveMatchingState(long matchingId) => _matchingStates.TryRemove(matchingId, out _);

    private PlayerSummonState GetOrCreatePlayerState(long matchingId, long playerId)
    {
        var matchingState = _matchingStates.GetOrAdd(matchingId,
            _ => new ConcurrentDictionary<long, PlayerSummonState>());
        return matchingState.GetOrAdd(playerId, _ => new PlayerSummonState());
    }

    private static SummonStoneSnapshot CreateSnapshot(PlayerSummonState state) =>
        new(state.StoneCount, state.SuccessfulSummonCount, GetCost(state.SuccessfulSummonCount));

    private static int GetCost(int successfulSummonCount) =>
        SummonCosts[Math.Clamp(successfulSummonCount, 0, SummonCosts.Length - 1)];

    private static int SelectOrbItemId(long matchingId, long playerId, int successfulSummonCount)
    {
        // The result is keyed only by match, player and successful summon count.
        // Region, target and afterimage-affinity state never participate in this RNG path.
        ulong value = unchecked((ulong)matchingId * 0x9E3779B185EBCA87UL)
                      ^ unchecked((ulong)playerId * 0xC2B2AE3D27D4EB4FUL)
                      ^ unchecked((ulong)(successfulSummonCount + 1) * 0x165667B19E3779F9UL);
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        value ^= value >> 31;
        // The opening board needs an immediate way to fight afterimages.
        // Only the first summon is restricted; later summons retain the full pool.
        int[] pool = successfulSummonCount == 0 ? OpeningAttackPool : SummonPool;
        return pool[(int)(value % (ulong)pool.Length)];
    }

    private sealed class PlayerSummonState
    {
        public object SyncRoot { get; } = new();
        public int StoneCount { get; set; }
        public int SuccessfulSummonCount { get; set; }
        public bool HasStartingStones { get; set; }
    }
}

public readonly record struct SummonStoneSnapshot(int StoneCount, int SuccessfulSummonCount, int NextCost);

public readonly record struct SummonOrbAttempt(bool Success, ErrorCode ErrorCode, int ItemId,
    InGameItemInfo? AddedItem, SummonStoneSnapshot State)
{
    public static SummonOrbAttempt Failed(ErrorCode errorCode, SummonStoneSnapshot state) =>
        new(false, errorCode, 0, null, state);
}
