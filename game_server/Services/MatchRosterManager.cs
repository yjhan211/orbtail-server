using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.helpers;

namespace game_server.services;

/// <summary>
///     매치 하나의 참가 명단과 탈락 기록을 관리한다.
///     탈락 기록, 최후 1인 판정, 게임 결과 생성.
/// </summary>
public class MatchRosterManager
{
    // MatchRuntime마다 별도 객체를 만들므로 다른 매치의 참가자가 섞이지 않는다.
    private readonly long _matchingId;
    private MatchRosterState? _state;
    private readonly ILogger _logger;

    internal MatchRosterManager(long matchingId, ILogger logger)
    {
        _matchingId = matchingId;
        _state = new MatchRosterState { MatchingId = matchingId };
        _logger = logger;
    }

    internal void Release() => Interlocked.Exchange(ref _state, null);

    /// <summary>플레이어 접속 시 참가 명단에 등록한다.</summary>
    public void RegisterEntry(RosterEntry link)
    {
        var state = Volatile.Read(ref _state) ?? throw new InvalidOperationException($"Match is not available: {_matchingId}");

        if (state.Entries.ContainsKey(link.PlayerId))
        {
            _logger.LogDebug("Roster entry already registered: MatchingId={MatchingId}, PlayerId={PlayerId}", _matchingId,
                link.PlayerId);
            return;
        }

        state.Entries[link.PlayerId] = link;
        state.AliveCount = state.Entries.Count;
        _logger.LogInformation("로스터 엔트리 등록: MatchingId={MatchingId}, PlayerId={PlayerId}, 현재 {Count}명",
            _matchingId, link.PlayerId, state.AliveCount);
    }

    /// <summary>
    ///     플레이어의 로스터 엔트리 조회
    /// </summary>
    public RosterEntry? GetEntry(long playerId)
    {
        if (Volatile.Read(ref _state) is not { } state) return null;
        return state.Entries.GetValueOrDefault(playerId);
    }

    public void UpdatePlayerProfile(
        long playerId,
        string? name,
        IEnumerable<int>? wearItemIds)
    {
        if (Volatile.Read(ref _state) is not { } state ||
            !state.Entries.TryGetValue(playerId, out var entry))
            return;

        lock (entry)
        {
            entry.Name = name ?? string.Empty;
            entry.WearItemIdList = wearItemIds?.ToList() ?? [];
        }
    }

    public MatchPlayerProfile? GetPlayerProfile(long playerId)
    {
        if (Volatile.Read(ref _state) is not { } state ||
            !state.Entries.TryGetValue(playerId, out var entry))
            return null;

        lock (entry)
            return new MatchPlayerProfile(entry.Name, entry.WearItemIdList.ToArray());
    }

    private static bool IsAliveLink(RosterEntry? link)
    {
        return link != null
               && link.Status != PlayerMatchStatus.ELIMINATED
               && link.Status != PlayerMatchStatus.SPECTATING;
    }

    /// <summary>
    ///     플레이어 탈락 처리. 로스터에 탈락 사유·순위를 기록한다.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public PlayerEliminationTransition TryEliminatePlayer(long playerId, EliminationReason reason,
        long attackerPlayerId = 0, AreaType eliminatedArea = AreaType.None, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false, int forcedRank = 0, int finalOrbTier = 0)
    {
        var affected = new Dictionary<long, PlayerMatchStatus>();

        if (Volatile.Read(ref _state) is not { } state) return new PlayerEliminationTransition(false, affected);
        if (!state.Entries.TryGetValue(playerId, out var link)) return new PlayerEliminationTransition(false, affected);
        if (link.Status == PlayerMatchStatus.ELIMINATED) return new PlayerEliminationTransition(false, affected);

        link.Status = PlayerMatchStatus.ELIMINATED;
        link.EliminationReason = reason;
        link.EliminatedAt = DateTime.UtcNow;
        link.AttackerPlayerId = attackerPlayerId;
        link.EliminatedArea = eliminatedArea;
        link.IsAreaClosureElimination = isAreaClosureElimination;
        link.IsOvertimeElimination = isOvertimeElimination;
        link.FinalOrbTier = finalOrbTier;
        link.EliminationRank = forcedRank > 0 ? forcedRank : state.AliveCount;
        state.AliveCount--;

        _logger.LogInformation("플레이어 탈락: MatchingId={MatchingId}, PlayerId={PlayerId}, 사유={Reason}, 생존={Alive}",
            _matchingId, playerId, reason, state.AliveCount);

        affected[playerId] = PlayerMatchStatus.ELIMINATED;

        return new PlayerEliminationTransition(true, affected);
    }

    /// <summary>
    ///     최후의 1인 판정
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public (bool isGameOver, long? winnerId) CheckGameOver()
    {
        if (Volatile.Read(ref _state) is not { } state)
            return (false, null);

        if (state.Entries.IsEmpty)
            return (false, null);

        var activePlayers = state.Entries.Values
            .Where(l => l.Status != PlayerMatchStatus.ELIMINATED && l.Status != PlayerMatchStatus.SPECTATING)
            .ToList();

        if (activePlayers.Count <= 1)
        {
            long? winnerId = activePlayers.FirstOrDefault()?.PlayerId;
            return (true, winnerId);
        }

        return (false, null);
    }

    /// <summary>
    ///     게임 결과 데이터 생성 (전체 로스터 공개)
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public List<(long playerId,
        EliminationReason reason, PlayerMatchStatus finalStatus, DateTime? eliminatedAt,
        long attackerPlayerId, AreaType eliminatedArea, bool isAreaClosureElimination,
        bool isOvertimeElimination, int eliminationRank, int finalOrbTier)> BuildGameResult()
    {
        if (Volatile.Read(ref _state) is not { } state)
            return new();

        var result = new List<(long, EliminationReason, PlayerMatchStatus, DateTime?, long,
            AreaType, bool, bool, int, int)>();

        foreach (var link in state.Entries.Values)
        {
            result.Add((link.PlayerId,
                link.EliminationReason, link.Status, link.EliminatedAt, link.AttackerPlayerId,
                link.EliminatedArea, link.IsAreaClosureElimination, link.IsOvertimeElimination,
                link.EliminationRank, link.FinalOrbTier));
        }

        return result;
    }

}

public sealed record PlayerEliminationTransition(
    bool Applied,
    Dictionary<long, PlayerMatchStatus> AffectedPlayers);

public sealed record MatchPlayerProfile(string Name, IReadOnlyList<int> WearItemIdList);

public class MatchRosterState
{
    public long MatchingId { get; set; }
    public ConcurrentDictionary<long, RosterEntry> Entries { get; set; } = new();
    public int AliveCount { get; set; }
}

public class RosterEntry
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
