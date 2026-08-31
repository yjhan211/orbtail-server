using network.common;
using network.common.data.models;

namespace game_server.services;

// #294 — GameServer.SwarmArena 파셜에 산개돼 있던 매치 상태 딕셔너리 37개를 도메인별
// 상태 홀더 4개로 묶는다. 로직은 GameServer에 남고 상태 소유·매치 정리만 여기로 온다:
// 각 홀더의 RemoveMatchingState(matchingId) 하나가 84줄짜리 수동 정리를 대체하고,
// 필드 추가 시 정리 누락(매치 간 누수)을 홀더 안에서 잡는다.

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
    public readonly Dictionary<(long MatchingId, long OwnerId, long ItemUid), int> OrbCutCracks = new();

    // 오브 내구 보너스 (#226 방어 강화 = 내구 모델): 기본 내구 1 + 보너스.
    // 파괴·매치 정리에서 함께 지운다. 크랙은 유지된다(방어 강화가 균열을 지우지 않는다).
    public readonly Dictionary<(long MatchingId, long PlayerId, long ItemUid), int> OrbDurabilityBonus = new();

    // 파도 폭탄: 오브 uid 기반 고유 위상으로 첫 발동을 흩뿌린다.
    public readonly Dictionary<(long MatchingId, long PlayerId, long ItemUid), DateTime>
        WaveBombNextDropAtUtc = new();
    public readonly List<(long MatchingId, long OwnerId, AreaType Area, Vector3f Position, int Damage,
        float Radius, int SourceItemId, DateTime ExplodeAtUtc)> PendingWaveBombs = new();

    public void RemoveMatchingState(long matchingId)
    {
        RemoveWhere(OrbTrails, key => key.MatchingId == matchingId);
        RemoveWhere(TrailLastTickPositions, key => key.MatchingId == matchingId);
        RemoveWhere(OrbCutLatches, key => key.MatchingId == matchingId);
        RemoveWhere(CutRetaliationWindows, key => key.MatchingId == matchingId);
        RemoveWhere(OrbCutCracks, key => key.MatchingId == matchingId);
        RemoveWhere(OrbDurabilityBonus, key => key.MatchingId == matchingId);
        RemoveWhere(WaveBombNextDropAtUtc, key => key.MatchingId == matchingId);
        PendingWaveBombs.RemoveAll(bomb => bomb.MatchingId == matchingId);
    }

    internal static void RemoveWhere<TKey, TValue>(
        Dictionary<TKey, TValue> map, Func<TKey, bool> predicate) where TKey : notnull
    {
        foreach (var key in map.Keys.Where(predicate).ToList())
            map.Remove(key);
    }
}

/// <summary>성장 카드 3택 오퍼 상태 (#226 단계 C).</summary>
public sealed class SwarmGrowthOfferStore
{
    public readonly Dictionary<(long MatchingId, long PlayerId), SwarmGrowthOfferState> Offers = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), int> PreviewCost = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> OfferResentAtUtc = new();
    public int NextOfferId = 1;

    public void RemoveMatchingState(long matchingId)
    {
        SwarmTrailCombatState.RemoveWhere(Offers, key => key.MatchingId == matchingId);
        SwarmTrailCombatState.RemoveWhere(PreviewCost, key => key.MatchingId == matchingId);
        SwarmTrailCombatState.RemoveWhere(OfferResentAtUtc, key => key.MatchingId == matchingId);
    }
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
    public readonly Dictionary<(long MatchingId, long PlayerId, AreaType Area, int CampIndex), DateTime>
        CampSkipUntilUtc = new();

    // 봇 오염 자연 회복: 마지막 피격 후 유예가 지나면 초당 일정량 회복한다.
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> LastDamagedAtUtc = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> NextRecoveryAtUtc = new();

    public void RemoveMatchingState(long matchingId)
    {
        SwarmTrailCombatState.RemoveWhere(AreaMemory, key => key.MatchingId == matchingId);
        FleeDirective.RemoveWhere(key => key.MatchingId == matchingId);
        SwarmTrailCombatState.RemoveWhere(LastTrailCutAtUtc, key => key.MatchingId == matchingId);
        Wounded.RemoveWhere(key => key.MatchingId == matchingId);
        SwarmTrailCombatState.RemoveWhere(ChaseLogThrottle, key => key.MatchingId == matchingId);
        SwarmTrailCombatState.RemoveWhere(CampSkipUntilUtc, key => key.MatchingId == matchingId);
        SwarmTrailCombatState.RemoveWhere(LastDamagedAtUtc, key => key.MatchingId == matchingId);
        SwarmTrailCombatState.RemoveWhere(NextRecoveryAtUtc, key => key.MatchingId == matchingId);
    }
}

/// <summary>매치 페이싱·피격 대기열·포위·계측 서명·샌드박스 등 잡화 상태.</summary>
public sealed class SwarmMatchPacingState
{
    public readonly HashSet<(long MatchingId, long PlayerId)> StartingOrbGrantedPlayers = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), float> PvpCorruptionCarry = new();
    public readonly Dictionary<(long MatchingId, long PlayerId),
        (Vector3f Position, DateTime At, bool Moving, DateTime StoppedAtUtc)> MovementSamples = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), (int ItemId, int Hp)> FrontOrbHp = new();

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

    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> EncircleCandidateSinceUtc = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> EncircleCooldownUtc = new();

    // 개발용 절단 더미 샌드박스 (#226 실험장).
    public readonly HashSet<long> CutDummyAutoSetupDone = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> CutDummyRefillAtUtc = new();

    public void RemoveMatchingState(long matchingId)
    {
        StartingOrbGrantedPlayers.RemoveWhere(key => key.MatchingId == matchingId);
        SwarmTrailCombatState.RemoveWhere(PvpCorruptionCarry, key => key.MatchingId == matchingId);
        SwarmTrailCombatState.RemoveWhere(MovementSamples, key => key.MatchingId == matchingId);
        SwarmTrailCombatState.RemoveWhere(FrontOrbHp, key => key.MatchingId == matchingId);
        PendingMonsterHits.RemoveAll(hit => hit.MatchingId == matchingId);
        PendingPvpHits.RemoveAll(hit => hit.MatchingId == matchingId);
        AnchorOrphanCount.Remove(matchingId);
        AnchorProbeAtUtc.Remove(matchingId);
        JamRankingsSignature.Remove(matchingId);
        TimeoutEndedMatchings.Remove(matchingId);
        MatchFallbackAnchorUtc.Remove(matchingId);
        ContactProbeAtUtc.Remove(matchingId);
        FieldStateAnnounced.Remove(matchingId);
        SwarmTrailCombatState.RemoveWhere(EncircleCandidateSinceUtc, key => key.MatchingId == matchingId);
        SwarmTrailCombatState.RemoveWhere(EncircleCooldownUtc, key => key.MatchingId == matchingId);
        CutDummyAutoSetupDone.Remove(matchingId);
        SwarmTrailCombatState.RemoveWhere(CutDummyRefillAtUtc, key => key.MatchingId == matchingId);
    }
}
