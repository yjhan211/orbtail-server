using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.combat;

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

/// <summary>오브 트레일·절단·반격 창·내구 상태.</summary>
public sealed class MatchTrailCombatState
{
    public readonly Dictionary<long, List<Vector3f>> OrbTrails = new();
    public readonly Dictionary<long, Vector3f> TrailLastTickPositions = new();

    // ItemUid별 절단 래치 (단계 A): 마지막 타격 시각 — 중복 억제·이탈 재무장의 기준.
    public readonly Dictionary<(long CutterId, long ItemUid), DateTime> OrbCutLatches = new();
    public readonly Dictionary<(long CutterId, long VictimId), SwarmRetaliationWindow>
        CutRetaliationWindows = new();

    // 오브 내구 보너스 (#226 방어 강화 = 내구 모델): 기본 내구 1 + 보너스.
    // 파괴·매치 정리에서 함께 지운다.
    public readonly Dictionary<(long PlayerId, long ItemUid), int> OrbDurabilityBonus = new();

}
