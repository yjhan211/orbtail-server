using network.common.data.models;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.matches;

/// <summary>
///     매치 하나의 참가자 프로필과 생존·탈락 상태를 관리한다.
///     입장 시 참가자를 등록하고, 탈락 시 사유·시각·순위를 기록한다.
///     참가자 프로필은 패킷과 결과 생성에 사용하며, 생존자 수로 게임 종료 여부를 판단한다.
///     매치 정리 후에는 참가 정보를 비우고 새 등록을 거부한다.
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
            _logger.LogInformation("Roster entry registered: MatchingId={MatchingId}, PlayerId={PlayerId}, Count={Count}", _matchingId, participant.PlayerId, _aliveCount);
        }
    }

    public MatchParticipant? GetParticipant(long playerId)
    {
        lock (_rosterLock)
        {
            if (_released || !_participants.TryGetValue(playerId, out var entry))
            {
                return null;
            }

            return entry;
        }
    }

    public List<PlayerInfo> GetPlayerProfiles()
    {
        lock (_rosterLock)
        {
            return _participants.Values.Select(participant => participant.Profile).ToList();
        }
    }

    public bool TryEliminatePlayer(long playerId, EliminationReason reason,
        long attackerPlayerId = 0, AreaType eliminatedArea = AreaType.None, int forcedRank = 0, int finalOrbTier = 0)
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
            participant.FinalOrbTier = finalOrbTier;
            participant.EliminationRank = forcedRank > 0 ? forcedRank : _aliveCount;
            _aliveCount--;

            _logger.LogInformation("플레이어 탈락: MatchingId={MatchingId}, PlayerId={PlayerId}, 사유={Reason}, 생존={Alive}", _matchingId, playerId, reason, _aliveCount);
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
        long attackerPlayerId, AreaType eliminatedArea, int eliminationRank, int finalOrbTier)> BuildGameResult()
    {
        lock (_rosterLock)
        {
            if (_released)
                return new();

            var result = new List<(long, EliminationReason, PlayerMatchStatus, DateTime?, long,
                AreaType, int, int)>();

            foreach (var participant in _participants.Values)
            {
                result.Add((participant.PlayerId,
                    participant.EliminationReason, participant.Status, participant.EliminatedAt, participant.AttackerPlayerId,
                    participant.EliminatedArea,
                    participant.EliminationRank, participant.FinalOrbTier));
            }

            return result;
        }
    }

}


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
