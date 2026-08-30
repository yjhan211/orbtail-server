using System.Collections.Concurrent;
using network.common;
using network.common.data;

namespace game_server.services;

/// <summary>
///     단일 매칭 인스턴스의 규칙 상태
///     매치 시작 시 모든 규칙이 셔플되어 큐에 들어감
///     플레이어들이 쪽지를 사용하면 순차적으로 규칙을 발견
/// </summary>
public class MatchingAreaRuleState
{
    // 적용된 모든 규칙 ID 목록 (InteractRuleManager용)
    private readonly List<int> _allRuleIds;

    private readonly object _queueLock = new();

    // 셔플된 규칙 큐 (쪽지 사용 시 순차적으로 반환)
    private readonly Queue<int> _shuffledRuleQueue;

    public MatchingAreaRuleState(Random? random = null)
    {
        random ??= new Random();

        // 모든 규칙 가져오기
        var allRules = GameAreaRuleData.SelectRulesForAllAreas(random);

        // 복도 규칙과 나머지 규칙 분리
        var corridorRules = new List<int>();
        var otherRules = new List<int>();

        foreach (var kvp in allRules)
            foreach (int ruleId in kvp.Value)
                if (kvp.Key.IsCorridor())
                    corridorRules.Add(ruleId);
                else
                    otherRules.Add(ruleId);

        // 첫 번째 복도 규칙 선택 (랜덤)
        int? firstCorridorRule = null;
        if (corridorRules.Count > 0)
        {
            int idx = random.Next(corridorRules.Count);
            firstCorridorRule = corridorRules[idx];
            corridorRules.RemoveAt(idx);
        }

        // 나머지 규칙들 합치고 셔플
        var remainingRules = corridorRules.Concat(otherRules).ToList();
        Shuffle(remainingRules, random);

        // 복도 규칙을 첫 번째로, 나머지는 셔플된 순서로
        _allRuleIds = new List<int>();
        if (firstCorridorRule.HasValue)
            _allRuleIds.Add(firstCorridorRule.Value);
        _allRuleIds.AddRange(remainingRules);

        // 큐에 넣기
        _shuffledRuleQueue = new Queue<int>(_allRuleIds);
    }

    /// <summary>
    ///     남은 규칙 개수
    /// </summary>
    public int RemainingRuleCount
    {
        get
        {
            lock (_queueLock)
            {
                return _shuffledRuleQueue.Count;
            }
        }
    }

    /// <summary>
    ///     적용된 모든 규칙 ID 목록
    /// </summary>
    public List<int> AllRuleIds => [.. _allRuleIds];

    private static void Shuffle<T>(IList<T> list, Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    ///     큐에서 다음 규칙 ID를 가져옴 (쪽지 사용 시 호출)
    ///     모든 플레이어가 같은 순서로 규칙을 발견
    /// </summary>
    /// <returns>규칙 ID, 큐가 비어있으면 0</returns>
    public int DequeueNextRule()
    {
        lock (_queueLock)
        {
            return _shuffledRuleQueue.Count > 0 ? _shuffledRuleQueue.Dequeue() : 0;
        }
    }
}

/// <summary>
///     MatchingId별로 규칙 상태를 관리하는 매니저
///     각 매칭 인스턴스는 셔플된 규칙 큐를 가짐
/// </summary>
public class AreaRuleManager
{
    private readonly ConcurrentDictionary<long, MatchingAreaRuleState> _matchingStates = new();
    private Action<string>? _logAction;

    public void Initialize(Action<string>? logAction = null)
    {
        _logAction = logAction;
        _matchingStates.Clear();
        _logAction?.Invoke("AreaRuleManager: Initialized");
    }

    /// <summary>
    ///     매칭 인스턴스의 규칙 상태를 가져오거나 새로 생성
    /// </summary>
    private MatchingAreaRuleState GetOrCreateMatchingState(long matchingId, Random? random = null)
    {
        return _matchingStates.GetOrAdd(matchingId, id =>
        {
            var state = new MatchingAreaRuleState(random);
            _logAction?.Invoke(
                $"AreaRuleManager: Created rule state for MatchingId={id}, Rules=[{string.Join(", ", state.AllRuleIds)}]");
            return state;
        });
    }

    /// <summary>
    ///     쪽지 아이템 사용 시 다음 규칙 ID 반환
    ///     모든 플레이어가 같은 순서로 규칙을 발견
    /// </summary>
    /// <returns>규칙 ID, 큐가 비어있으면 0</returns>
    public int GetRuleForNote(long matchingId)
    {
        var state = GetOrCreateMatchingState(matchingId);
        int ruleId = state.DequeueNextRule();
        _logAction?.Invoke(
            $"AreaRuleManager: MatchingId={matchingId} note used, RuleId={ruleId}, remaining={state.RemainingRuleCount}");
        return ruleId;
    }

    /// <summary>
    ///     매칭 종료 시 해당 매칭의 상태 정리
    /// </summary>
    public void RemoveMatchingState(long matchingId)
    {
        if (_matchingStates.TryRemove(matchingId, out _))
            _logAction?.Invoke($"AreaRuleManager: Removed state for MatchingId={matchingId}");
    }
}
