using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.services;

/// <summary>
///     인스턴스별 마니또 체인 관리.
///     탈락, 체인 단절, 시한부/해방 상태, 색출, 게임 종료 판정.
/// </summary>
public class ManittoChainManager
{
    // matchingId → 체인 상태
    private readonly ConcurrentDictionary<long, MatchingChainState> _states = new();
    private readonly ILogger _logger;

    public ManittoChainManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     플레이어 접속 시 링크 등록 (누적)
    /// </summary>
    public void RegisterLink(long matchingId, ChainLink link)
    {
        var state = _states.GetOrAdd(matchingId, _ => new MatchingChainState { MatchingId = matchingId });

        if (state.Links.ContainsKey(link.PlayerId))
        {
            _logger.LogDebug("이미 등록된 링크: MatchingId={MatchingId}, PlayerId={PlayerId}", matchingId, link.PlayerId);
            return;
        }

        state.Links[link.PlayerId] = link;
        state.AliveCount = state.Links.Count;
        _logger.LogInformation("마니또 체인 링크 등록: MatchingId={MatchingId}, PlayerId={PlayerId}, 현재 {Count}명",
            matchingId, link.PlayerId, state.AliveCount);
    }

    /// <summary>
    ///     플레이어의 체인 링크 조회
    /// </summary>
    public ChainLink? GetLink(long matchingId, long playerId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return null;
        return state.Links.GetValueOrDefault(playerId);
    }

    /// <summary>
    ///     해당 매칭에 등록된 모든 플레이어/봇의 직책 목록 (#87 — 폐쇄 셔플 우선순위 결정용).
    /// </summary>
    public List<JobTitle> GetMatchingJobs(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return new();
        return state.Links.Values.Select(l => l.MyJobTitle).Distinct().ToList();
    }

    /// <summary>
    ///     색출 시도. 1회 한정.
    ///     반환: (성공 여부, 에러코드)
    /// </summary>
    public (bool isCorrect, ErrorCode errorCode) TryDetect(long matchingId, long detecterId, long targetPlayerId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return (false, ErrorCode.GAME_NOT_STARTED);

        if (!state.Links.TryGetValue(detecterId, out var detecterLink))
            return (false, ErrorCode.PLAYER_NOT_FOUND);

        // 이미 색출 사용했으면 거절
        if (detecterLink.HasUsedDetection)
            return (false, ErrorCode.DETECT_ALREADY_USED);

        // 최종 2인이면 비활성화
        if (state.AliveCount <= 2)
            return (false, ErrorCode.DETECT_NOT_AVAILABLE);

        // 탈락자/시한부 등은 색출 불가
        if (detecterLink.Status != ManittoStatus.ACTIVE && detecterLink.Status != ManittoStatus.FREED)
            return (false, ErrorCode.DETECT_NOT_AVAILABLE);

        detecterLink.HasUsedDetection = true;

        // 지목한 대상이 실제로 내 마니또(스토커)인지 확인
        // 내 마니또 = 나를 타겟으로 가진 플레이어
        var myManitto = state.Links.Values.FirstOrDefault(l => l.TargetPlayerId == detecterId);
        bool isCorrect = myManitto?.PlayerId == targetPlayerId;

        _logger.LogInformation("색출 시도: DetecterId={Detecter}, Target={Target}, 실제마니또={Manitto}, 결과={Result}",
            detecterId, targetPlayerId, myManitto?.PlayerId, isCorrect ? "적중" : "실패");

        return (isCorrect, ErrorCode.SUCCESS);
    }

    /// <summary>
    ///     플레이어 탈락 처리. 체인 단절 + 영향받는 플레이어 상태 변경.
    ///     반환: 영향받는 플레이어 목록 (playerId → 새 상태)
    /// </summary>
    public Dictionary<long, ManittoStatus> EliminatePlayer(long matchingId, long playerId, EliminationReason reason)
    {
        var affected = new Dictionary<long, ManittoStatus>();

        if (!_states.TryGetValue(matchingId, out var state)) return affected;
        if (!state.Links.TryGetValue(playerId, out var link)) return affected;
        if (link.Status == ManittoStatus.ELIMINATED) return affected;

        link.Status = ManittoStatus.ELIMINATED;
        link.EliminationReason = reason;
        link.EliminatedAt = DateTime.UtcNow;
        state.AliveCount--;

        _logger.LogInformation("플레이어 탈락: MatchingId={MatchingId}, PlayerId={PlayerId}, 사유={Reason}, 생존={Alive}",
            matchingId, playerId, reason, state.AliveCount);

        affected[playerId] = ManittoStatus.ELIMINATED;

        // 1) 탈락자의 타겟(▓▓) → 마니또(스토커)로부터 해방
        long freedPlayerId = link.TargetPlayerId;
        if (state.Links.TryGetValue(freedPlayerId, out var freedLink) &&
            freedLink.Status == ManittoStatus.ACTIVE)
        {
            freedLink.Status = ManittoStatus.FREED;
            affected[freedPlayerId] = ManittoStatus.FREED;
            _logger.LogInformation("해방: PlayerId={PlayerId} (마니또 {Manitto} 탈락)", freedPlayerId, playerId);
        }

        // 2) 탈락자의 마니또 → 시한부 진입 (타겟 상실)
        var manittoLink = state.Links.Values.FirstOrDefault(l => l.TargetPlayerId == playerId);
        if (manittoLink != null && manittoLink.Status == ManittoStatus.ACTIVE)
        {
            manittoLink.Status = ManittoStatus.TERMINAL;
            affected[manittoLink.PlayerId] = ManittoStatus.TERMINAL;
            _logger.LogInformation("시한부: PlayerId={PlayerId} (타겟 {Target} 탈락)", manittoLink.PlayerId, playerId);
        }

        return affected;
    }

    /// <summary>
    ///     최후의 1인 판정
    /// </summary>
    public (bool isGameOver, long? winnerId) CheckGameOver(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return (false, null);

        var activePlayers = state.Links.Values
            .Where(l => l.Status != ManittoStatus.ELIMINATED && l.Status != ManittoStatus.SPECTATING)
            .ToList();

        if (activePlayers.Count <= 1)
        {
            long? winnerId = activePlayers.FirstOrDefault()?.PlayerId;
            return (true, winnerId);
        }

        return (false, null);
    }

    /// <summary>
    ///     시한부 플레이어 조회 (정신력 감소 틱 대상)
    /// </summary>
    public List<long> GetTerminalPlayers(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return new List<long>();

        return state.Links.Values
            .Where(l => l.Status == ManittoStatus.TERMINAL)
            .Select(l => l.PlayerId)
            .ToList();
    }

    /// <summary>
    ///     시간 초과 시 승자 판정: 생존자 중 자원 총합 최대
    /// </summary>
    public long? DetermineWinnerByResources(long matchingId,
        Func<long, (int stamina, int corruption, int maxCorruption)> getResources)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return null;

        var alive = state.Links.Values
            .Where(l => l.Status != ManittoStatus.ELIMINATED && l.Status != ManittoStatus.SPECTATING)
            .ToList();

        if (alive.Count == 0) return null;
        if (alive.Count == 1) return alive[0].PlayerId;

        // 자원 총합 = Stamina + (MaxCorruption - Corruption)
        long winnerId = alive
            .Select(l =>
            {
                var (stamina, corruption, maxCorruption) = getResources(l.PlayerId);
                return (l.PlayerId, Score: stamina + (maxCorruption - corruption));
            })
            .OrderByDescending(x => x.Score)
            .First().PlayerId;

        return winnerId;
    }

    /// <summary>
    ///     게임 결과 데이터 생성 (체인 전체 공개)
    /// </summary>
    public List<(long playerId, JobTitle job, long targetId, long manittoId,
        EliminationReason reason, ManittoStatus finalStatus)> BuildGameResult(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return new();

        var links = state.Links.Values.ToList();
        var result = new List<(long, JobTitle, long, long, EliminationReason, ManittoStatus)>();

        foreach (var link in links)
        {
            // 이 플레이어의 마니또 = 이 플레이어를 타겟으로 가진 링크
            long manittoId = links.FirstOrDefault(l => l.TargetPlayerId == link.PlayerId)?.PlayerId ?? 0;
            result.Add((link.PlayerId, link.MyJobTitle, link.TargetPlayerId, manittoId,
                link.EliminationReason, link.Status));
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

public class MatchingChainState
{
    public long MatchingId { get; set; }
    public ConcurrentDictionary<long, ChainLink> Links { get; set; } = new();
    public int AliveCount { get; set; }
}

public class ChainLink
{
    public long PlayerId { get; set; }
    public long TargetPlayerId { get; set; }  // 내가 돌봐야 하는 ▓▓
    public JobTitle MyJobTitle { get; set; }
    public JobTitle TargetJobTitle { get; set; }
    public ManittoStatus Status { get; set; } = ManittoStatus.ACTIVE;
    public bool HasUsedDetection { get; set; }
    public EliminationReason EliminationReason { get; set; } = EliminationReason.NONE;
    public DateTime? EliminatedAt { get; set; }
}
