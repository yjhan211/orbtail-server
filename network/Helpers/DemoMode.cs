using network.common;

namespace network.helpers;

/// <summary>
///     5/11 영상 + 5/14 심사용 빌드 시연 모드.
///     환경변수 <c>DEMO_MODE=LB</c> 활성화 시 LB 도서위원 시연 시나리오로 게임 진행.
///     - 결정론 시드 (H1) — 봇/색출/폐쇄 셔플 모두 동일 시드 사용
///     - 폐쇄 셔플 보호 (H2) — 도서관·교실2 면역
///     - 봇 race 페이스 캡 (H4) — 봇 7:00 이전 결합 차단
///     - 봇 4명 직책 + 6단계 동선 스크립트 (W7)
///     - 매칭 직책 강제 (W7) — user_server에서 5인 매칭 시 LB+BR/DC/SC/HE 보장
///
///     설계 보고서: design/outputs/audit-reports/2026-05-03_시연시나리오-종합보고서.md
/// </summary>
public static class DemoMode
{
    public static bool IsActive => Environment.GetEnvironmentVariable("DEMO_MODE") == "LB";

    /// <summary>결정론 시드 (H1). 모든 RNG가 이 값으로 인자.</summary>
    public const int Seed = 20260511;

    /// <summary>데모 폐쇄 시작 지연: 게임 시작 후 60초.</summary>
    public const int ClosureStartDelaySec = 60;

    /// <summary>데모 폐쇄 간격: 첫 폐쇄 이후 30초마다.</summary>
    public const int ClosureIntervalSec = 30;

    /// <summary>봇 race 페이스 캡 (H4). 봇은 게임 시작 후 이 시간 이전 결합 차단.</summary>
    public const int BotRaceMinSeconds = 420; // 7분

    /// <summary>H3 — SC 봇이 색출을 강제로 시도하는 게임 경과 시간 (06:40).</summary>
    public const int ScDetectionAttemptSeconds = 400;

    /// <summary>H8 — HE 봇이 강제 탈락하는 게임 경과 시간 (12:00, 시한부 narrative).</summary>
    public const int HeForcedEliminationSeconds = 720;

    /// <summary>H6 — BR 봇이 함정 흔적을 도서관에 1회 배치하는 게임 경과 시간 (09:30).</summary>
    public const int BrTracePlacementSeconds = 570;

    /// <summary>H6 — BR 봇 함정 흔적이 부착될 도서관 인터랙터블 ID (701000044 = 하늘색 배낭).</summary>
    public const int BrTraceInteractId = 701000044;

    /// <summary>H6 — BR 봇 함정 흔적 위치 (도서관).</summary>
    public const AreaType BrTraceArea = AreaType.Library;

    /// <summary>H6 — BR 봇 함정 흔적 description (영상 narrative '마니또의 양면 선택' cut).</summary>
    public const string BrTraceDescription = "도서관 의자 옆에 누군가 머무른 흔적이 남아 있습니다.";

    /// <summary>
    ///     W3 — 봇 6단계 동선 스크립트. JobTitle별 (경과초, 위치) 웨이포인트 리스트.
    ///     elapsed >= sec 중 가장 큰 sec의 area로 봇 위치 강제 (시계열 순서).
    ///     영상 비트 정합:
    ///     02:50 SC 도서관 1:1 / 04:30 DC 도서관 / 06:00 BR 도서관(회복 흔적) / 09:30 BR 도서관(함정 발동) / HE 강당 캠핑
    /// </summary>
    public static readonly Dictionary<JobTitle, IReadOnlyList<(int elapsedSec, AreaType area)>> BotMovementScript = new()
    {
        [JobTitle.BROADCAST_MEMBER] = new[]
        {
            (0, AreaType.BroadcastRoom),  // 시작 (4F 방송실)
            (60, AreaType.Corridor4F),    // 01:00 4F 복도
            (180, AreaType.Corridor2F),   // 03:00 2F 도서관 인근 진입
            (360, AreaType.Library),      // 06:00 도서관 (회복 흔적 배치)
            (480, AreaType.Corridor2F),   // 08:00 후퇴
            (570, AreaType.Library)       // 09:30 도서관 재방문 (09:40 함정 흔적 발동 위치)
        },
        [JobTitle.DISCIPLINE_MEMBER] = new[]
        {
            (0, AreaType.Corridor1F),     // 시작 (1F 복도)
            (60, AreaType.AdminOffice),   // 01:00 행정실 (DC 발견 구역)
            (180, AreaType.Corridor2F),   // 03:00 2F 진입
            (270, AreaType.Library),      // 04:30 도서관 진입 (04:50 LB 조우 셋업)
            (360, AreaType.Corridor2F),   // 06:00 후퇴
            (480, AreaType.AdminOffice)   // 08:00 1F 회귀
        },
        [JobTitle.SCIENCE_MEMBER] = new[]
        {
            (0, AreaType.ExamRoom),       // 시작 (3F 고사실)
            (120, AreaType.Corridor3F),   // 02:00 3F 복도 경유
            (170, AreaType.Library),      // 02:50 도서관 (1:1 with LB)
            (240, AreaType.Corridor3F),   // 04:00 후퇴
            (300, AreaType.ExamRoom)      // 05:00 고사실 회귀 (06:40 색출 위치)
        }
        // HEALTH_MEMBER 단일 영역 고정 항목 제거 — 직책 큐 따라 자연 walking 시각.
        // 12:00 강제 탈락(H8)은 ProcessBotTick의 HeForcedEliminationSeconds 흐름에서 그대로 유지.
    };

    /// <summary>시연 매칭 인원 — 시연자 1명 + 봇 4명.</summary>
    public const int MatchPlayerCount = 5;

    /// <summary>시연 매칭 시 시연자(본인)가 배치될 체인 인덱스 (BR=0 → LB=1 → DC=2 → SC=3 → HE=4 → BR).</summary>
    public const int PlayerChainIndex = 1;

    /// <summary>시연 시나리오 봇 4명 직책 (체인: BR → LB(본인) → DC → SC → HE → BR).</summary>
    public static readonly JobTitle[] BotJobOrder =
    {
        JobTitle.BROADCAST_MEMBER,   // BR — 본인의 마니또
        JobTitle.DISCIPLINE_MEMBER,  // DC — 본인의 ▓▓
        JobTitle.SCIENCE_MEMBER,     // SC — 06:40 색출 실패 → 탈락
        JobTitle.HEALTH_MEMBER       // HE — 12:00 강제 탈락 (시한부 narrative)
    };

    /// <summary>
    ///     시연 매칭 체인 직책 순서. 인덱스 0~4가 그대로 ManittoChain 인덱스에 대응.
    ///     PlayerChainIndex(=1) 위치만 시연자(LB), 나머지 4자리는 봇.
    /// </summary>
    public static readonly JobTitle[] ChainJobOrder =
    {
        JobTitle.BROADCAST_MEMBER,   // index 0: BR 봇 (본인의 마니또)
        JobTitle.LIBRARY_COMMITTEE,  // index 1: LB 본인
        JobTitle.DISCIPLINE_MEMBER,  // index 2: DC 봇 (본인의 ▓▓)
        JobTitle.SCIENCE_MEMBER,     // index 3: SC 봇
        JobTitle.HEALTH_MEMBER       // index 4: HE 봇
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
    ///     시연용 폐쇄 시퀀스. 60초 후 교실3(3-1)부터 시작하고 이후 30초마다 다음 구역을 닫는다.
    ///     도서관/교실2는 ProtectedAreas로 자동 제외.
    /// </summary>
    public static readonly List<AreaType> ForcedClosureSequence = new()
    {
        AreaType.Classroom3,   // 3-1
        AreaType.ExamRoom,     // 고사실
        AreaType.AdminOffice,  // 행정실
        AreaType.Storage,      // 창고
        AreaType.Gym,          // 강당
        AreaType.StaffRoom     // 교무실
    };
}
