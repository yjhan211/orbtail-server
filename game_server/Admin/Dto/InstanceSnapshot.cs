namespace game_server.admin.dto;

/// <summary>
///     운영 어드민용 활성 인스턴스 스냅샷 DTO (System.Text.Json 직렬화)
/// </summary>
public class InstanceSnapshot
{
    public long MatchingId { get; set; }
    public string MapId { get; set; } = "";
    public int PlayerCount { get; set; }
    public int AliveCount { get; set; }
    public double ElapsedSeconds { get; set; }
    public int RoundNumber { get; set; }
    public int TotalRounds { get; set; }
    public string RoundPhase { get; set; } = "";
    public int RoundRemainingSeconds { get; set; }
    public int RoundPhaseDurationSeconds { get; set; }
    public bool RoundSessionEnded { get; set; }
    public List<string> ClosedAreas { get; set; } = [];
    public List<PlayerSnapshot> Players { get; set; } = [];

    /// <summary>구역 폐쇄 스케줄 상세 (풀 스냅샷 전용)</summary>
    public ClosureSnapshot? Closure { get; set; }
}

/// <summary>
///     구역 폐쇄 스케줄 스냅샷
/// </summary>
public class ClosureSnapshot
{
    /// <summary>전체 폐쇄 시퀀스 (AreaType 정수 목록)</summary>
    public List<int> ClosureSequence { get; set; } = [];

    /// <summary>폐쇄 시퀀스 한글명 목록 (ClosureSequence와 1:1 대응)</summary>
    public List<string> AreaNames { get; set; } = [];

    /// <summary>이미 폐쇄된 구역 (AreaType 정수 목록)</summary>
    public List<int> ClosedAreaIds { get; set; } = [];

    /// <summary>다음 폐쇄 예정 구역 (없으면 -1)</summary>
    public int NextClosureAreaType { get; set; } = -1;

    /// <summary>다음 폐쇄 예정 Unix 타임스탬프 초 단위 (없으면 -1)</summary>
    public long NextClosureAtUnix { get; set; } = -1;

    /// <summary>다음 폐쇄 카운트다운 잔여 초 (없으면 -1)</summary>
    public int NextClosureSecondsLeft { get; set; } = -1;

    /// <summary>30초 경고 활성 여부</summary>
    public bool WarningActive { get; set; }

    /// <summary>이 인스턴스에 적용된 폐쇄 시작 딜레이 (초)</summary>
    public int StartDelaySec { get; set; }

    /// <summary>이 인스턴스에 적용된 폐쇄 간격 (초)</summary>
    public int IntervalSec { get; set; }
}

/// <summary>
///     v0.2.0 — 부품 진행 상세 (어드민 전용). 직책별 7부품(소재 4 + 중간재 2 + 최종 1).
/// </summary>

/// <summary>
///     활성 인스턴스 목록 응답 DTO
/// </summary>
public class InstanceListResponse
{
    public int TotalInstances { get; set; }
    public int TotalPlayers { get; set; }
    public List<InstanceSummary> Instances { get; set; } = [];
}

/// <summary>
///     인스턴스 요약 DTO (목록 뷰용)
/// </summary>
public class InstanceSummary
{
    public long MatchingId { get; set; }
    public string MapId { get; set; } = "";
    public int PlayerCount { get; set; }
    public int AliveCount { get; set; }
    public double ElapsedSeconds { get; set; }
    public int RoundNumber { get; set; }
    public int TotalRounds { get; set; }
    public string RoundPhase { get; set; } = "";
    public int RoundRemainingSeconds { get; set; }
    public int RoundPhaseDurationSeconds { get; set; }
    public bool RoundSessionEnded { get; set; }
    public List<string> ClosedAreas { get; set; } = [];
}
