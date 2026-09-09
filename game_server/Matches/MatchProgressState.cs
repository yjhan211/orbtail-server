using game_server.combat;

namespace game_server.matches;

public sealed class MatchProgressState
{
    public readonly HashSet<(long MatchingId, long PlayerId)> StartingOrbGrantedPlayers = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), float> PvpDamageCarry = new();
    public readonly List<PendingMonsterHit> PendingMonsterHits = new();
    public readonly List<(long MatchingId, ProximityCombatAttack Attack, DateTime DueAtUtc)> PendingPvpHits = new();
    public readonly Dictionary<long, int> AnchorOrphanCount = new();
    public readonly Dictionary<long, DateTime> AnchorProbeAtUtc = new();
    public readonly Dictionary<long, string> JamRankingsSignature = new();
    public readonly HashSet<long> TimeoutEndedMatchings = new();
    public readonly Dictionary<long, DateTime> MatchFallbackAnchorUtc = new();
    public readonly Dictionary<long, DateTime> ContactProbeAtUtc = new();
    public readonly HashSet<long> FieldStateAnnounced = new();
    public int? LastCountdownSecondsPublished { get; set; }
}
