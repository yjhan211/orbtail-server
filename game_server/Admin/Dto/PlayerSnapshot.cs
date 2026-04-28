namespace game_server.admin.dto;

/// <summary>
///     운영 어드민용 플레이어 코어 스냅샷 DTO (System.Text.Json 직렬화)
/// </summary>
public class PlayerSnapshot
{
    public long PlayerId { get; set; }
    public string Area { get; set; } = "";
    public int Stamina { get; set; }
    public int Corruption { get; set; }
    public string ManittoStatus { get; set; } = "";
    public long TargetPlayerId { get; set; }
    public bool IsBot { get; set; }
    public bool IsEliminated { get; set; }
    public int MissionStep { get; set; }
    public int MissionTotalSteps { get; set; }
    public bool MissionCompleted { get; set; }
    public long? ManittoOfMe { get; set; }       // 나를 타겟으로 가진 플레이어 (마니또)
    public string ChainStatus { get; set; } = ""; // ChainLink.Status 문자열

    /// <summary>직책 이름 (풀 스냅샷 전용)</summary>
    public string JobTitle { get; set; } = "";

    /// <summary>직책별 전체 미션 단계 목록 (풀 스냅샷 전용)</summary>
    public List<MissionFullStep> AllSteps { get; set; } = [];
}
