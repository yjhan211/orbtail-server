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
    public string PlayerMatchStatus { get; set; } = "";
    public long TargetPlayerId { get; set; }
    public bool IsBot { get; set; }
    public bool IsEliminated { get; set; }
    public long? WatcherOfMe { get; set; }       // 나를 타겟으로 가진 플레이어 (마니또)
    public string ChainStatus { get; set; } = ""; // RosterEntry.Status 문자열
}
