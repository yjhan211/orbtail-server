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
    public const int PassiveIncomeIntervalSeconds = 30;
    public const int PassiveIncomeAmount = 1;

    private const int BaseSummonCost = 2;

    // 회복 오브(107000040) 퇴역 (#219, 2026-08-08): 오브 HP 모델에서 오염 회복은 죽음과
    // 무관해져 기능이 죽었고, "회복만 남는" 막다른 상태의 원천이었다. SB 문법대로 회복은
    // 새 오브 영입(만피 새 몸)이 담당한다. 아이템 정의·연출·틱 코드는 게이트 보존 —
    // 치유 클래스로 부활 검토 시 재사용 (이슈 #219 매핑 8번).
    // 공급 차단 토글(SWARM_SUN/WAVE_ORB_ENABLED=false)이면 소환 풀에서 그 색이 빠진다.
    private static readonly int[] SummonPool = BuildSummonPool();

    private static int[] BuildSummonPool()
    {
        var pool = new List<int>();
        if (Config.SWARM_SUN_ORB_ENABLED) pool.Add(107000010);
        if (Config.SWARM_WIND_ORB_ENABLED) pool.Add(107000020);
        if (Config.SWARM_WAVE_ORB_ENABLED) pool.Add(107000030);
        return pool.ToArray();
    }
    private static readonly int[] OpeningAttackPool = SummonPool
        .Where(itemId => !OrbData.IsRecoveryOrb(itemId))
        .ToArray();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, PlayerSummonState>> _matchingStates = new();

    public IReadOnlyList<int> PoolItemIds => SummonPool;
    // #219 M2: 시작 소환석 5 — 첫 개봉(비용 5) 한 번을 보장해 개전 직후 드래프트 맛을 먼저 보여준다.
    // 이후 소환석은 몹 처치로 번다 (빈손이 되면 개봉 무료 규칙이 재기를 보장).
    public static int InitialSummonStoneCount => 5;

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

    public SummonStoneSnapshot AdvancePassiveIncome(
        long matchingId,
        long playerId,
        int elapsedSeconds,
        out int awardedStones)
    {
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
        {
            awardedStones = 0;
            if (elapsedSeconds <= 0)
                return CreateSnapshot(state);

            state.PassiveIncomeElapsedSeconds =
                checked(state.PassiveIncomeElapsedSeconds + elapsedSeconds);
            int grantCount = state.PassiveIncomeElapsedSeconds / PassiveIncomeIntervalSeconds;
            if (grantCount <= 0)
                return CreateSnapshot(state);

            state.PassiveIncomeElapsedSeconds %= PassiveIncomeIntervalSeconds;
            awardedStones = checked(grantCount * PassiveIncomeAmount);
            state.StoneCount = checked(state.StoneCount + awardedStones);
            return CreateSnapshot(state);
        }
    }
    public SummonStoneSnapshot GetSnapshot(long matchingId, long playerId)
    {
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
            return CreateSnapshot(state);
    }

    /// <summary>성장 성공 카운트 N 조회 (#226 C 잔여) — 비용 곡선·HUD 표시의 단일 출처.</summary>
    public int GetGrowthSuccessCount(long matchingId, long playerId)
    {
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
            return state.GrowthSuccessCount;
    }

    /// <summary>
    ///     카드별 성장 성공 카운트 (#229): 소환·공격 강화·방어 강화가 각자 자기 곡선을 탄다.
    ///     하나로 묶으면 오브를 늘릴수록 강화가 비싸지고 강화할수록 소환이 비싸져,
    ///     세 선택이 서로의 값을 밀어 올리는 경제가 된다.
    /// </summary>
    public int GetGrowthSuccessCount(long matchingId, long playerId, int cardIndex)
    {
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
            return state.GrowthSuccessByCard.GetValueOrDefault(cardIndex);
    }

    /// <summary>성장 카드 성공 적용 시 1회 호출 — 전체 N과 카드별 N을 함께 누적한다.</summary>
    public void RecordGrowthSuccess(long matchingId, long playerId, int cardIndex = -1)
    {
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
        {
            state.GrowthSuccessCount++;
            if (cardIndex >= 0)
                state.GrowthSuccessByCard[cardIndex] =
                    state.GrowthSuccessByCard.GetValueOrDefault(cardIndex) + 1;
        }
    }

    /// <summary>
    ///     오브를 잃은 만큼 소환 카운터를 되돌린다 (#229). 소환 비용은 "내가 몇 개 샀는가"를
    ///     따라가는 값이라, 잘려 나간 오브가 값에 그대로 남으면 뒤처진 사람이 재건조차 못 한다.
    ///     잃은 개수만큼만 내린다 — 한 개만 잃어도 곡선이 0으로 리셋되면 싼 오브를 일부러
    ///     내주고 값을 초기화하는 수가 최적해가 된다.
    /// </summary>
    public void RefundGrowthSuccess(long matchingId, long playerId, int cardIndex, int count)
    {
        if (count <= 0)
            return;

        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
        {
            int current = state.GrowthSuccessByCard.GetValueOrDefault(cardIndex);
            state.GrowthSuccessByCard[cardIndex] = Math.Max(0, current - count);
            state.GrowthSuccessCount = Math.Max(0, state.GrowthSuccessCount - Math.Min(count, current));
        }
    }

    /// <summary>
    ///     소환 없이 소환석만 차감 (#226 단계 C): 성장 카드(강화·철갑)와 상자 개봉이 쓴다.
    ///     잔액 부족이면 아무것도 바꾸지 않는다.
    /// </summary>
    public bool TrySpendStones(long matchingId, long playerId, int amount, out SummonStoneSnapshot snapshot)
    {
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
        {
            if (amount < 0 || state.StoneCount < amount)
            {
                snapshot = CreateSnapshot(state);
                return false;
            }

            state.StoneCount -= amount;
            snapshot = CreateSnapshot(state);
            return true;
        }
    }

    public SummonOrbAttempt TrySummon(long matchingId, long playerId, Func<int, InGameItemInfo?> grantItem,
        int choiceIndex = 0, int? costOverride = null, int? exactItemId = null)
    {
        ArgumentNullException.ThrowIfNull(grantItem);
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
        {
            // costOverride: 스웜 P0-c의 보유 오브 비례 비용. 기본 곡선(소환 횟수 삼각수)을 대체한다.
            int cost = costOverride ?? GetCost(state.SuccessfulSummonCount);
            if (state.StoneCount < cost)
                return SummonOrbAttempt.Failed(ErrorCode.INSUFFICIENT_CURRENCY, CreateSnapshot(state));

            // exactItemId: #219 3택 드래프트 — 클라이언트가 고른 색을 그대로 지급한다.
            int itemId;
            if (exactItemId.HasValue)
            {
                itemId = exactItemId.Value;
            }
            else
            {
                int[] candidates = ComputeSummonCandidates(matchingId, playerId, state.SuccessfulSummonCount);
                itemId = candidates[Math.Clamp(choiceIndex, 0, candidates.Length - 1)];
            }
            InGameItemInfo? item = grantItem(itemId);
            if (item == null)
                return SummonOrbAttempt.Failed(ErrorCode.INVENTORY_FULL, CreateSnapshot(state));

            state.StoneCount -= cost;
            state.SuccessfulSummonCount++;
            return new SummonOrbAttempt(true, ErrorCode.SUCCESS, itemId, item, CreateSnapshot(state));
        }
    }

    /// <summary>
    ///     소환 2택 후보. 결과가 (매치, 플레이어, 소환 횟수)에만 결정론적으로 묶여 있으므로
    ///     대기 상태·만료 타이머 없이 미리 공개할 수 있고, 재접속에도 같은 값이 복원된다.
    ///     후보 0은 기존 단일 소환 스트림과 동일해 선택 인덱스를 보내지 않는 요청과 호환된다.
    /// </summary>
    public int[] GetSummonCandidates(long matchingId, long playerId)
    {
        var state = GetOrCreatePlayerState(matchingId, playerId);
        lock (state.SyncRoot)
            return ComputeSummonCandidates(matchingId, playerId, state.SuccessfulSummonCount);
    }

    private static int[] ComputeSummonCandidates(long matchingId, long playerId, int successfulSummonCount)
    {
        int first = SelectOrbItemId(matchingId, playerId, successfulSummonCount);
        int second = SelectOrbItemId(matchingId, playerId, successfulSummonCount, salt: 1);
        if (second != first)
            return [first, second];

        // 같은 오브 두 개는 선택이 아니다. 결정론을 유지한 채 풀의 다음 항목으로 민다.
        int[] pool = successfulSummonCount == 0 ? OpeningAttackPool : SummonPool;
        second = pool[(Array.IndexOf(pool, second) + 1) % pool.Length];
        return [first, second];
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

    private static int GetCost(int successfulSummonCount)
    {
        long summonNumber = (long)Math.Max(0, successfulSummonCount) + 1;
        long cost = Math.Max(BaseSummonCost, summonNumber * (summonNumber + 1) / 2);
        return (int)Math.Min(int.MaxValue, cost);
    }

    private static int SelectOrbItemId(long matchingId, long playerId, int successfulSummonCount, int salt = 0)
    {
        // The result is keyed only by match, player and successful summon count.
        // Region, target and afterimage-affinity state never participate in this RNG path.
        // salt 0은 XOR 항등이라 기존 단일 소환 스트림을 그대로 보존한다. 2택의 두 번째
        // 후보만 salt 1로 분기한다.
        ulong value = unchecked((ulong)matchingId * 0x9E3779B185EBCA87UL)
                      ^ unchecked((ulong)playerId * 0xC2B2AE3D27D4EB4FUL)
                      ^ unchecked((ulong)(successfulSummonCount + 1) * 0x165667B19E3779F9UL)
                      ^ unchecked((ulong)salt * 0x27D4EB2F165667C5UL);
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
        public int PassiveIncomeElapsedSeconds { get; set; }

        // 성장 카드 성공 선택 횟수 N (#226 C 잔여): 비용 곡선 3+floor(N/3)의 단일 출처.
        // 오브가 잘려도 줄지 않는다 — 절단이 성장 시간을 초기화하지 못하게.
        public int GrowthSuccessCount { get; set; }

        // 카드별 성장 성공 수 (#229): 0 소환 · 1 공격 강화 · 2 방어 강화.
        public Dictionary<int, int> GrowthSuccessByCard { get; } = new();
    }
}

public readonly record struct SummonStoneSnapshot(int StoneCount, int SuccessfulSummonCount, int NextCost);

public readonly record struct SummonOrbAttempt(bool Success, ErrorCode ErrorCode, int ItemId,
    InGameItemInfo? AddedItem, SummonStoneSnapshot State)
{
    public static SummonOrbAttempt Failed(ErrorCode errorCode, SummonStoneSnapshot state) =>
        new(false, errorCode, 0, null, state);
}
