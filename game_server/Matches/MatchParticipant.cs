using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>매치 참가자 한 명의 프로필과 탈락 결과. MatchRuntime이 보관한다.</summary>
public class MatchParticipant
{
    public long PlayerId => Profile.PlayerId;
    public required PlayerInfo Profile { get; init; }
    public PlayerMatchStatus Status { get; set; } = PlayerMatchStatus.ACTIVE;
    public EliminationReason EliminationReason { get; set; } = EliminationReason.NONE;
    public DateTime? EliminatedAt { get; set; }
    public long AttackerPlayerId { get; set; }
    public AreaType EliminatedArea { get; set; } = AreaType.None;
    public int EliminationRank { get; set; }
    public int FinalOrbTier { get; set; }
}
