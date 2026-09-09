using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.matches;

/// <summary>
///     매치 하나의 참가 명단과 탈락 기록을 관리한다.
///     탈락 기록, 최후 1인 판정, 게임 결과 생성.
/// </summary>
public class MatchRoster
{
    private readonly long _matchingId;
    private readonly object _rosterLock = new();
    private readonly Dictionary<long, MatchParticipant> _participants = new();
    private int _aliveCount;
    private bool _released;
    private readonly ILogger _logger;

    internal MatchRoster(long matchingId, ILogger logger)
    {
        _matchingId = matchingId;
        _logger = logger;
    }

    internal void Release()
    {
        lock (_rosterLock)
        {
            _released = true;
            _participants.Clear();
            _aliveCount = 0;
        }
    }

    public void RegisterParticipant(MatchParticipant participant)
    {
        lock (_rosterLock)
        {
            if (_released)
            {
                throw new InvalidOperationException($"Match is not available: {_matchingId}");
            }

            if (!_participants.TryAdd(participant.PlayerId, participant))
            {
                _logger.LogDebug("Roster entry already registered: MatchingId={MatchingId}, PlayerId={PlayerId}", _matchingId, participant.PlayerId);
                return;
            }

            _aliveCount = _participants.Count;
            _logger.LogInformation("로스터 엔트리 등록: MatchingId={MatchingId}, PlayerId={PlayerId}, 현재 {Count}명", _matchingId, participant.PlayerId, _aliveCount);
        }
    }

    public MatchPlayerProfile? GetPlayerProfile(long playerId)
    {
        lock (_rosterLock)
        {
            if (_released || !_participants.TryGetValue(playerId, out var entry))
            {
                return null;
            }

            return new MatchPlayerProfile(entry.Name, entry.WearItemIdList.ToArray());
        }
    }

    public bool TryEliminatePlayer(long playerId, EliminationReason reason,
        long attackerPlayerId = 0, AreaType eliminatedArea = AreaType.None, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false, int forcedRank = 0, int finalOrbTier = 0)
    {
        lock (_rosterLock)
        {

            if (_released)
            {
                return false;
            }
            if (!_participants.TryGetValue(playerId, out var participant)) return false;
            if (participant.Status == PlayerMatchStatus.ELIMINATED) return false;

            participant.Status = PlayerMatchStatus.ELIMINATED;
            participant.EliminationReason = reason;
            participant.EliminatedAt = DateTime.UtcNow;
            participant.AttackerPlayerId = attackerPlayerId;
            participant.EliminatedArea = eliminatedArea;
            participant.IsAreaClosureElimination = isAreaClosureElimination;
            participant.IsOvertimeElimination = isOvertimeElimination;
            participant.FinalOrbTier = finalOrbTier;
            participant.EliminationRank = forcedRank > 0 ? forcedRank : _aliveCount;
            _aliveCount--;

            _logger.LogInformation("플레이어 탈락: MatchingId={MatchingId}, PlayerId={PlayerId}, 사유={Reason}, 생존={Alive}",
                _matchingId, playerId, reason, _aliveCount);


            return true;
        }
    }

    public (bool isGameOver, long? winnerId) CheckGameOver()
    {
        lock (_rosterLock)
        {
            if (_released)
                return (false, null);

            if (_participants.Count == 0)
                return (false, null);

            var activePlayers = _participants.Values
                .Where(l => l.Status != PlayerMatchStatus.ELIMINATED && l.Status != PlayerMatchStatus.SPECTATING)
                .ToList();

            if (activePlayers.Count <= 1)
            {
                long? winnerId = activePlayers.FirstOrDefault()?.PlayerId;
                return (true, winnerId);
            }

            return (false, null);
        }
    }

    public List<(long playerId,
        EliminationReason reason, PlayerMatchStatus finalStatus, DateTime? eliminatedAt,
        long attackerPlayerId, AreaType eliminatedArea, bool isAreaClosureElimination,
        bool isOvertimeElimination, int eliminationRank, int finalOrbTier)> BuildGameResult()
    {
        lock (_rosterLock)
        {
            if (_released)
                return new();

            var result = new List<(long, EliminationReason, PlayerMatchStatus, DateTime?, long,
                AreaType, bool, bool, int, int)>();

            foreach (var participant in _participants.Values)
            {
                result.Add((participant.PlayerId,
                    participant.EliminationReason, participant.Status, participant.EliminatedAt, participant.AttackerPlayerId,
                    participant.EliminatedArea, participant.IsAreaClosureElimination, participant.IsOvertimeElimination,
                    participant.EliminationRank, participant.FinalOrbTier));
            }

            return result;
        }
    }

}

public sealed record MatchPlayerProfile(string Name, IReadOnlyList<int> WearItemIdList);

public class MatchParticipant
{
    public long PlayerId { get; set; }
    public PlayerMatchStatus Status { get; set; } = PlayerMatchStatus.ACTIVE;
    public EliminationReason EliminationReason { get; set; } = EliminationReason.NONE;
    public DateTime? EliminatedAt { get; set; }
    public long AttackerPlayerId { get; set; }
    public AreaType EliminatedArea { get; set; } = AreaType.None;
    public bool IsAreaClosureElimination { get; set; }
    public bool IsOvertimeElimination { get; set; }
    public int EliminationRank { get; set; }
    public int FinalOrbTier { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<int> WearItemIdList { get; set; } = [];
}
