using network.common;

namespace game_server.services;

/// <summary>
///     5/11 영상 + 5/14 심사용 빌드 시연 모드.
///     환경변수 <c>DEMO_MODE=LB</c> 활성화 시 LB 도서위원 시연 시나리오로 게임 진행.
///     - 결정론 시드 (H1) — 봇/색출/폐쇄 셔플 모두 동일 시드 사용
///     - 폐쇄 셔플 보호 (H2) — 도서관·교실2 면역
///     - 봇 race 페이스 캡 (H4) — 봇 7:00 이전 결합 차단
///     - 봇 4명 직책 + 6단계 동선 스크립트 (W7)
///
///     설계 보고서: design/outputs/audit-reports/2026-05-03_시연시나리오-종합보고서.md
/// </summary>
public static class DemoMode
{
    public static bool IsActive => Environment.GetEnvironmentVariable("DEMO_MODE") == "LB";

    /// <summary>결정론 시드 (H1). 모든 RNG가 이 값으로 인자.</summary>
    public const int Seed = 20260511;

    /// <summary>봇 race 페이스 캡 (H4). 봇은 게임 시작 후 이 시간 이전 결합 차단.</summary>
    public const int BotRaceMinSeconds = 420; // 7분

    /// <summary>시연 시나리오 봇 4명 직책 (체인: BR → LB(본인) → DC → SC → HE → BR).</summary>
    public static readonly JobTitle[] BotJobOrder =
    {
        JobTitle.BROADCAST_MEMBER,   // BR — 본인의 마니또
        JobTitle.DISCIPLINE_MEMBER,  // DC — 본인의 ▓▓
        JobTitle.SCIENCE_MEMBER,     // SC — 06:40 색출 실패 → 탈락
        JobTitle.HEALTH_MEMBER       // HE — 12:00 강제 탈락 (시한부 narrative)
    };

    /// <summary>시연자(본인) 직책: LB 도서위원.</summary>
    public const JobTitle PlayerJob = JobTitle.LIBRARY_COMMITTEE;

    /// <summary>
    ///     폐쇄 셔플 보호 (H2). LB 시연에서 출구(도서관) + LB_M2(교실2) 면역.
    ///     ApplyJobAwareShuffle보다 더 엄격 — 시퀀스에서 완전히 제외.
    /// </summary>
    public static readonly HashSet<AreaType> ProtectedAreas = new()
    {
        AreaType.Library,    // LB 출구 (race cutscene 영역)
        AreaType.Classroom2  // LB_M2 (봉인 인장)
    };

    /// <summary>
    ///     시연용 폐쇄 시퀀스. 영상 시나리오 5:30 교실3, 8:30 방송실 cut과 정합.
    ///     도서관/교실2는 ProtectedAreas로 자동 제외.
    /// </summary>
    public static readonly List<AreaType> ForcedClosureSequence = new()
    {
        AreaType.Classroom3,    // 5:30 — LB_M2 회수 완료, BR 봇은 더 이상 사용 안 함
        AreaType.BroadcastRoom, // 8:30 — BR 봇 race 차단
        AreaType.ExamRoom,      // 11:00 — SC 봇 부품 소실 (이미 탈락)
        AreaType.AdminOffice,   // 12:30 — 영상 cut 외
        AreaType.Storage,       // 13:30+ — 백업
        AreaType.Gym,
        AreaType.Junkyard,
        AreaType.Corridor1F,
        AreaType.Corridor2F,
        AreaType.Corridor3F
    };
}
