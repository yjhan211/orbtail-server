using game_server.matches.combat;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.states;

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

/// <summary>매치 진행 중 유지하는 피격 대기열·카운트다운·초기 지급·계측·개발 옵션 상태.</summary>
public sealed class MatchProgressState
{
    private readonly Random _criticalRng = new();

    /// <summary>
    ///     Keeps one match's critical-damage draw stream independent from other matches. Callers
    ///     run under the enclosing match execution gate, so this Random is never used concurrently.
    /// </summary>
    internal bool RollCritical(double chance) => _criticalRng.NextDouble() < chance;

    public readonly HashSet<(long MatchingId, long PlayerId)> StartingOrbGrantedPlayers = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), float> PvpDamageCarry = new();

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
