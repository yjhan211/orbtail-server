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
    public List<string> ClosedAreas { get; set; } = [];
    public List<PlayerSnapshot> Players { get; set; } = [];
}

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
    public List<string> ClosedAreas { get; set; } = [];
}
