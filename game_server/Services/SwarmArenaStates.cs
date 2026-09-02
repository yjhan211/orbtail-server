using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

// #294 후속 — GameServer.SwarmArena 파셜에 산개돼 있던 상태를 도메인별 홀더로 묶고,
// matchingId가 소유하는 aggregate에서 함께 생성·폐기한다. 기존 key shape는 단계적으로 줄인다.

/// <summary>
///     Owns the mutable Swarm state for exactly one matching id. The initial state holders keep
///     their established key shapes while the migration is in progress; newly migrated holders use
///     match-local keys. Every holder lifetime is bounded by this aggregate instead of GameServer.
/// </summary>
public sealed class SwarmMatchRuntime
{
    internal SwarmMatchRuntime(
        long matchingId,
        SwarmGrowthOfferIdSequence growthOfferIds,
        SwarmCrossfireEventIdSequence crossfireEventIds)
    {
        MatchingId = matchingId;
        GrowthOffers = new SwarmGrowthOfferStore();
        GrowthOfferCoordinator = new SwarmGrowthOfferCoordinator(
            matchingId,
            GrowthOffers,
            growthOfferIds);
        Crossfire = new SwarmCrossfireState(matchingId, crossfireEventIds);
    }

    public long MatchingId { get; }
    public SwarmTrailCombatState TrailCombat { get; } = new();
    public SwarmGrowthOfferStore GrowthOffers { get; }
    public SwarmGrowthOfferCoordinator GrowthOfferCoordinator { get; }
    public SwarmBotTacticalState BotTactics { get; } = new();
    public SwarmMatchPacingState Pacing { get; } = new();
    /// <summary>
    ///     Match-local outcome stream for explicit item-combine requests. It is intentionally
    ///     separate from combat critical rolls and is consumed only while the match runtime
    ///     monitor is held. The default seed is not a deterministic replay contract.
    /// </summary>
    internal Random ItemCombineRandom { get; } = new();
    /// <summary>봇 이동 틱 계측 창 — 기록은 매치 잠금 안, 바쁜 펄스 카운트는 잠금 밖 Interlocked.</summary>
    internal SwarmBotTickMetrics BotTickMetrics { get; } = new();
    public SwarmWindBladeState WindBlade { get; } = new();
    public SwarmOrbBoardState OrbBoard { get; } = new();
    public SwarmCrossfireState Crossfire { get; }
}

/// <summary>
///     Process-local index for match-owned Swarm runtimes. Removal drops the complete aggregate so
///     a match cannot leak one forgotten collection into the next match using the same process.
/// </summary>
public sealed class SwarmMatchRuntimeStore
{
    private readonly ConcurrentDictionary<long, SwarmMatchRuntime> _runtimes = new();
    private readonly SwarmGrowthOfferIdSequence _growthOfferIds = new();
    private readonly SwarmCrossfireEventIdSequence _crossfireEventIds = new();

    public int Count => _runtimes.Count;

    public SwarmMatchRuntime GetOrCreate(long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        return _runtimes.GetOrAdd(
            matchingId,
            static (id, sequences) => new SwarmMatchRuntime(
                id,
                sequences.GrowthOfferIds,
                sequences.CrossfireEventIds),
            (GrowthOfferIds: _growthOfferIds,
                CrossfireEventIds: _crossfireEventIds));
    }

    public bool TryGet(
        long matchingId,
        [NotNullWhen(true)] out SwarmMatchRuntime? runtime) =>
        _runtimes.TryGetValue(matchingId, out runtime);

    public bool Remove(long matchingId) => _runtimes.TryRemove(matchingId, out _);
}

/// <summary>
///     반격 보호 창 (#227 7단계): 절단자–피해자 <b>쌍</b>으로 연다. 같은 키가 다시 열리면
///     시간만 연장하고 집계는 이어간다 — 만료 시 CUT_RETALIATION_WINDOW로 결산한다.
/// </summary>
public sealed class SwarmRetaliationWindow
{
    public DateTime OpenedAtUtc;
    public DateTime ExpiresAtUtc;
    public int BlockedDamage;
    public int BlockedHits;
    public int BlockedCuts;
    public bool Retaliated;
    public AreaType OpenedArea;
}

/// <summary>
///     몬스터 피격 대기열 엔트리 — 비행 중 몹이 죽어도 사건은 잠근 위치에서 끝까지 처리된다.
/// </summary>
public readonly record struct PendingSwarmMonsterHit(
    long MatchingId,
    long CombatTargetId,
    long AttackerId,
    int Damage,
    DateTime ApplyAtUtc,
    int WeaponItemId = 0,
    long AttackerItemUid = 0,
    Vector3f? Origin = null,
    Vector3f? AnchorPosition = null);

/// <summary>오퍼 시점에 확정되는 성장 카드 구성 (#226 C 등급): 픽은 이 서술자를 그대로 집행한다.</summary>
// Cost는 대표값(가장 싼 카드)이고, 실제 차감·표시는 카드별 비용이 한다 (#229).
public readonly record struct SwarmGrowthOfferState(
    int OfferId, int Cost, int SpawnItemId, int EnhanceTargetTier, int ArmorCount,
    int CostSummon, int CostAttack, int CostDefense)
{
    // 카드 인덱스 — 클라·서버·로그 공유 (증식/강화/철갑).
    public const int CardMultiply = 0;
    public const int CardEnhance = 1;
    public const int CardArmor = 2;

    public int GetCost(int cardIndex) => cardIndex switch
    {
        CardMultiply => CostSummon,
        CardEnhance => CostAttack,
        CardArmor => CostDefense,
        _ => Cost
    };
}

/// <summary>오브 트레일·절단·반격 창·내구·파도 폭탄 상태 (#232 절단 전투 축).</summary>
public sealed class SwarmTrailCombatState
{
    public readonly Dictionary<(long MatchingId, long PlayerId), List<Vector3f>> OrbTrails = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), Vector3f> TrailLastTickPositions = new();

    // ItemUid별 절단 래치 (단계 A): 마지막 타격 시각 — 중복 억제·이탈 재무장의 기준.
    public readonly Dictionary<(long MatchingId, long CutterId, long ItemUid), DateTime> OrbCutLatches = new();
    public readonly Dictionary<(long MatchingId, long CutterId, long VictimId), SwarmRetaliationWindow>
        CutRetaliationWindows = new();

    // 오브 내구 보너스 (#226 방어 강화 = 내구 모델): 기본 내구 1 + 보너스.
    // 파괴·매치 정리에서 함께 지운다.
    public readonly Dictionary<(long MatchingId, long PlayerId, long ItemUid), int> OrbDurabilityBonus = new();

    // 파도 폭탄: 오브 uid 기반 고유 위상으로 첫 발동을 흩뿌린다.
    public readonly Dictionary<(long MatchingId, long PlayerId, long ItemUid), DateTime>
        WaveBombNextDropAtUtc = new();
    public readonly List<(long MatchingId, long OwnerId, AreaType Area, Vector3f Position, int Damage,
        float Radius, int SourceItemId, DateTime ExplodeAtUtc)> PendingWaveBombs = new();
}

/// <summary>성장 카드 3택 오퍼 상태 (#226 단계 C).</summary>
public sealed class SwarmGrowthOfferStore
{
    public readonly Dictionary<(long MatchingId, long PlayerId), SwarmGrowthOfferState> Offers = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), int> PreviewCost = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> OfferResentAtUtc = new();
}

/// <summary>봇 전술 상태 — 도주·부상·구역 기억·캠프 순례·회복 페이싱.</summary>
public sealed class SwarmBotTacticalState
{
    public readonly Dictionary<(long MatchingId, long PlayerId),
        (AreaType Area, AreaType PreviousArea, DateTime LeftAtUtc)> AreaMemory = new();

    // 이번 틱 지시가 도주·대피였는가 — 왕복 억제의 유일한 예외.
    public readonly HashSet<(long MatchingId, long PlayerId)> FleeDirective = new();

    // 절단 후 회수 창 (#226 F): 이 시간 동안은 약자 추격보다 바닥 소환석 회수가 먼저다.
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> LastTrailCutAtUtc = new();
    public readonly HashSet<(long MatchingId, long PlayerId)> Wounded = new();

    // 추격 로그 스로틀 — 같은 쌍은 3초에 한 번만 남긴다. 판단은 50ms마다 돈다.
    public readonly Dictionary<(long MatchingId, long ChaserId, long TargetId), DateTime> ChaseLogThrottle = new();

    // 봇 오염 자연 회복: 마지막 피격 후 유예가 지나면 초당 일정량 회복한다.
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> LastDamagedAtUtc = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> NextRecoveryAtUtc = new();
}

/// <summary>매치 페이싱·피격 대기열·계측 서명·샌드박스 등 잡화 상태.</summary>
public sealed class SwarmMatchPacingState
{
    private readonly Random _criticalRng = new();

    /// <summary>
    ///     Keeps one match's critical-damage draw stream independent from other matches. Callers
    ///     run under the enclosing match execution gate, so this Random is never used concurrently.
    /// </summary>
    internal bool RollCritical(double chance) => _criticalRng.NextDouble() < chance;

    public readonly HashSet<(long MatchingId, long PlayerId)> StartingOrbGrantedPlayers = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), float> PvpCorruptionCarry = new();

    public readonly List<PendingSwarmMonsterHit> PendingMonsterHits = new();

    // PvP 유도탄 착탄 지연 (2026-08-12 복귀): 발사 확정, 피해는 비행시간 뒤 — 회피 없음.
    public readonly List<(long MatchingId, ProximityCombatAttack Attack, DateTime DueAtUtc)> PendingPvpHits = new();

    public readonly Dictionary<long, int> AnchorOrphanCount = new();
    public readonly Dictionary<long, DateTime> AnchorProbeAtUtc = new();
    public readonly Dictionary<long, string> JamRankingsSignature = new();
    public readonly HashSet<long> TimeoutEndedMatchings = new();
    public readonly Dictionary<long, DateTime> MatchFallbackAnchorUtc = new();
    public readonly Dictionary<long, DateTime> ContactProbeAtUtc = new();
    public readonly HashSet<long> FieldStateAnnounced = new();

    /// <summary>마지막으로 방송한 카운트다운 남은 초 — 같은 초는 다시 보내지 않는다(재시도 없음).</summary>
    public int? LastCountdownSecondsPublished { get; set; }

    // 개발용 절단 더미 샌드박스 (#226 실험장).
    public readonly HashSet<long> CutDummyAutoSetupDone = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> CutDummyRefillAtUtc = new();
}

/// <summary>
///     바람 칼날의 틱·시동·피해자 면역·상처 상태. 이 객체 자체가 한 매치에 귀속되므로 내부 key에는
///     matching id를 반복하지 않는다. enclosing match execution gate가 모든 변경을 직렬화한다.
/// </summary>
public sealed class SwarmWindBladeState
{
    private readonly Dictionary<(long PlayerId, long ItemUid), DateTime> _nextTickAtUtc = new();
    private readonly Dictionary<(long PlayerId, long ItemUid), DateTime> _engagedAtUtc = new();
    private readonly Dictionary<long, DateTime> _victimImmuneUntilUtc = new();
    private readonly Dictionary<long, DateTime> _woundsUntilUtc = new();

    public bool TryBeginTick(long playerId, long itemUid, DateTime nowUtc, double intervalSeconds)
    {
        var key = (playerId, itemUid);
        if (_nextTickAtUtc.TryGetValue(key, out DateTime nextTickAtUtc) && nowUtc < nextTickAtUtc)
            return false;

        _nextTickAtUtc[key] = nowUtc.AddSeconds(intervalSeconds);
        return true;
    }

    public void ResetEngagement(long playerId, long itemUid) =>
        _engagedAtUtc.Remove((playerId, itemUid));

    public bool HasCompletedSpinup(long playerId, long itemUid, DateTime nowUtc, double durationSeconds)
    {
        var key = (playerId, itemUid);
        if (!_engagedAtUtc.TryGetValue(key, out DateTime engagedAtUtc))
        {
            engagedAtUtc = nowUtc;
            _engagedAtUtc[key] = engagedAtUtc;
        }

        return (nowUtc - engagedAtUtc).TotalSeconds >= durationSeconds;
    }

    public bool TryClaimVictimShock(long victimId, DateTime nowUtc, double immunitySeconds)
    {
        if (_victimImmuneUntilUtc.TryGetValue(victimId, out DateTime immuneUntilUtc) && nowUtc < immuneUntilUtc)
            return false;

        _victimImmuneUntilUtc[victimId] = nowUtc.AddSeconds(immunitySeconds);
        return true;
    }

    public void ApplyWound(long victimId, DateTime untilUtc) =>
        _woundsUntilUtc[victimId] = untilUtc;

    public bool IsWounded(long victimId, DateTime nowUtc) =>
        _woundsUntilUtc.TryGetValue(victimId, out DateTime untilUtc) && nowUtc < untilUtc;
}

/// <summary>
///     계열별 오브 강화 구매 횟수. 비용 계산이 읽는 매치 로컬 누적값만 소유하고, 소환석 소비와
///     인벤토리 교체는 GameServer의 기존 흐름에 남긴다.
/// </summary>
public sealed class SwarmOrbBoardState
{
    private readonly Dictionary<(long PlayerId, OrbColor Color), int> _familyUpgradeCounts = new();

    public int GetFamilyUpgradeCount(long playerId, OrbColor color) =>
        _familyUpgradeCounts.TryGetValue((playerId, color), out int count) ? count : 0;

    public int IncrementFamilyUpgradeCount(long playerId, OrbColor color)
    {
        int count = GetFamilyUpgradeCount(playerId, color) + 1;
        _familyUpgradeCounts[(playerId, color)] = count;
        return count;
    }
}
