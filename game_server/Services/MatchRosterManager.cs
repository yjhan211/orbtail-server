using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.helpers;

namespace game_server.services;

/// <summary>
///     인스턴스별 마니또 체인 관리.
///     탈락, 체인 단절, 시한부/해방 상태, 색출, 게임 종료 판정.
/// </summary>
public class MatchRosterManager
{
    // Survivor Royale replaces the legacy chain-break aftermath with combat and loot progression.
    // Keep link registration for compatibility, but do not propagate FREED/TERMINAL states.
    // matchingId → 체인 상태
    private readonly ConcurrentDictionary<long, MatchRosterState> _states = new();
    private readonly ILogger _logger;

    public MatchRosterManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     플레이어 접속 시 링크 등록 (누적)
    /// </summary>
    public void RegisterEntry(long matchingId, RosterEntry link)
    {
        var state = _states.GetOrAdd(matchingId, _ => new MatchRosterState { MatchingId = matchingId });

        if (state.Entries.TryGetValue(link.PlayerId, out var existing))
        {
            if (existing.TargetPlayerId != link.TargetPlayerId)
            {
                _logger.LogWarning(
                    "타깃 체인 링크 갱신: MatchingId={MatchingId}, PlayerId={PlayerId}, Target {OldTarget}->{NewTarget}",
                    matchingId, link.PlayerId, existing.TargetPlayerId, link.TargetPlayerId);

                existing.TargetPlayerId = link.TargetPlayerId;
                existing.Status = link.Status;
                existing.EliminationReason = link.EliminationReason;
                existing.EliminatedAt = link.EliminatedAt;
                existing.AttackerPlayerId = link.AttackerPlayerId;
                existing.EliminatedArea = link.EliminatedArea;
                existing.IsAreaClosureElimination = link.IsAreaClosureElimination;
                existing.IsOvertimeElimination = link.IsOvertimeElimination;
                existing.EliminationRank = link.EliminationRank;
                existing.FinalOrbTier = link.FinalOrbTier;
            }
            else
            {
                _logger.LogDebug("Roster link already registered: MatchingId={MatchingId}, PlayerId={PlayerId}", matchingId,
                    link.PlayerId);
            }

            return;
        }

        state.Entries[link.PlayerId] = link;
        state.AliveCount = state.Entries.Count;
        _logger.LogInformation("마니또 체인 링크 등록: MatchingId={MatchingId}, PlayerId={PlayerId}, 현재 {Count}명",
            matchingId, link.PlayerId, state.AliveCount);
    }

    /// <summary>
    ///     플레이어의 체인 링크 조회
    /// </summary>
    public RosterEntry? GetEntry(long matchingId, long playerId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return null;
        return state.Entries.GetValueOrDefault(playerId);
    }

    public void UpdatePlayerProfile(
        long matchingId,
        long playerId,
        string? name,
        IEnumerable<int>? wearItemIds)
    {
        if (!_states.TryGetValue(matchingId, out var state) ||
            !state.Entries.TryGetValue(playerId, out var entry))
            return;

        lock (entry)
        {
            entry.Name = name ?? string.Empty;
            entry.WearItemIdList = wearItemIds?.ToList() ?? [];
        }
    }

    public MatchPlayerProfile? GetPlayerProfile(long matchingId, long playerId)
    {
        if (!_states.TryGetValue(matchingId, out var state) ||
            !state.Entries.TryGetValue(playerId, out var entry))
            return null;

        lock (entry)
            return new MatchPlayerProfile(entry.Name, entry.WearItemIdList.ToArray());
    }

    public RosterEntry? FindWatcherOf(long matchingId, long playerId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return null;
        return state.Entries.Values.FirstOrDefault(l => l.TargetPlayerId == playerId);
    }

    private static bool IsAliveLink(RosterEntry? link)
    {
        return link != null
               && link.Status != PlayerMatchStatus.ELIMINATED
               && link.Status != PlayerMatchStatus.SPECTATING;
    }

    /// <summary>
    ///     플레이어 탈락 처리. 체인 단절 + 영향받는 플레이어 상태 변경.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public PlayerEliminationTransition TryEliminatePlayer(long matchingId, long playerId, EliminationReason reason,
        long attackerPlayerId = 0, AreaType eliminatedArea = AreaType.None, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false, int forcedRank = 0, int finalOrbTier = 0)
    {
        var affected = new Dictionary<long, PlayerMatchStatus>();

        if (!_states.TryGetValue(matchingId, out var state)) return new PlayerEliminationTransition(false, affected);
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
            matchingId, playerId, reason, state.AliveCount);

        affected[playerId] = PlayerMatchStatus.ELIMINATED;

        return new PlayerEliminationTransition(true, affected);
    }

    /// <summary>
    ///     최후의 1인 판정
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public (bool isGameOver, long? winnerId) CheckGameOver(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
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
    ///     게임 결과 데이터 생성 (체인 전체 공개)
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public List<(long playerId, long targetId, long watcherId,
        EliminationReason reason, PlayerMatchStatus finalStatus, DateTime? eliminatedAt,
        long attackerPlayerId, AreaType eliminatedArea, bool isAreaClosureElimination,
        bool isOvertimeElimination, int eliminationRank, int finalOrbTier)> BuildGameResult(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return new();

        var links = state.Entries.Values.ToList();
        var result = new List<(long, long, long, EliminationReason, PlayerMatchStatus, DateTime?, long,
            AreaType, bool, bool, int, int)>();

        foreach (var link in links)
        {
            // 이 플레이어의 감시자 = 이 플레이어를 타겟으로 가진 링크
            long watcherId = links.FirstOrDefault(l => l.TargetPlayerId == link.PlayerId)?.PlayerId ?? 0;
            result.Add((link.PlayerId, link.TargetPlayerId, watcherId,
                link.EliminationReason, link.Status, link.EliminatedAt, link.AttackerPlayerId,
                link.EliminatedArea, link.IsAreaClosureElimination, link.IsOvertimeElimination,
                link.EliminationRank, link.FinalOrbTier));
        }

        return result;
    }

    /// <summary>
    ///     매칭 정리
    /// </summary>
    public void CleanupMatching(long matchingId)
    {
        _states.TryRemove(matchingId, out _);
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
    public long TargetPlayerId { get; set; }  // 미니맵 타깃 마커 대상
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
