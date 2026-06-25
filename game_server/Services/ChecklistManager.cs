using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

public readonly record struct ChecklistChainContext(bool TargetAlive, bool ManittoAlive);

public sealed class ChecklistCompletionResult
{
    public ErrorCode ErrorCode { get; init; }
    public ChecklistTaskData? CompletedTask { get; init; }
    public ChecklistTaskData? NextGeneralJob { get; init; }
    public float AwardedScore { get; init; }
    public int AwardedContribution { get; init; }
    public bool ConsumedRequiredItem { get; init; }
    public InGameItemInfo? ConsumedItemUpdate { get; init; }
}

public sealed class ChecklistProgressAdvanceResult
{
    public bool Changed { get; init; }
    public ChecklistCompletionResult? Completion { get; init; }
}

public sealed class ChecklistPlayerContribution
{
    public long PlayerId { get; init; }
    public float GeneralJobScore { get; init; }
    public float ManittoRoleScore { get; init; }
    public float BonusScore { get; init; }
    public float TotalScore => GeneralJobScore + ManittoRoleScore + BonusScore;
    public int Contribution => (int)MathF.Round(TotalScore * ChecklistManager.ContributionScale, MidpointRounding.AwayFromZero);
}

public sealed class ChecklistManager(ILogger logger)
{
    internal const int ContributionScale = 10;
    private const bool EnableRequiredItemChecks = false;

    private readonly ConcurrentDictionary<long, MatchingChecklistState> _states = new();
    private Action<string>? _logAction;

    private float _generalRoundScoreCap = 1.5f;
    private int _generalScoringLimitCount = 5;
    private float _generalAfterLimitScore;
    private int _manittoRoleRoundLimitMin = 1;
    private int _manittoRoleRoundLimitMax = 2;

    public void Initialize(Action<string>? logAction = null)
    {
        _logAction = logAction;
        _states.Clear();
        ReloadRules();
        _logAction?.Invoke("ChecklistManager: Initialized");
    }

    private void ReloadRules()
    {
        _generalRoundScoreCap = GameChecklistData.GetRuleFloat("general_job_round_score_cap", 1.5f);
        _generalScoringLimitCount = GameChecklistData.GetRuleInt("general_job_scoring_limit_count", 5);
        _generalAfterLimitScore = GameChecklistData.GetRuleFloat("general_job_after_limit_score", 0f);
        _manittoRoleRoundLimitMin = GameChecklistData.GetRuleInt("manitto_role_round_limit_min", 1);
        _manittoRoleRoundLimitMax = GameChecklistData.GetRuleInt("manitto_role_round_limit_max", 2);
    }

    public void StartRound(
        long matchingId,
        int roundNumber,
        IEnumerable<long> playerIds,
        Func<long, ChecklistChainContext> chainContextResolver)
    {
        var state = _states.GetOrAdd(matchingId, id => new MatchingChecklistState(id));

        lock (state.SyncRoot)
        {
            state.RoundNumber = roundNumber;

            foreach (long playerId in playerIds.Distinct())
            {
                var playerState = state.GetOrCreatePlayerState(playerId);
                playerState.ResetForRound(roundNumber);
                GenerateRoundStartTasks(playerState, chainContextResolver(playerId));
            }
        }

        logger.LogInformation(
            "Checklist round started: MatchingId={MatchingId}, Round={Round}, Players={Players}",
            matchingId, roundNumber, string.Join(",", playerIds.Distinct().OrderBy(id => id)));
    }

    public List<ChecklistTaskData> GetActiveTasks(long matchingId, long playerId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return new List<ChecklistTaskData>();

        lock (state.SyncRoot)
        {
            return state.GetOrCreatePlayerState(playerId)
                .ActiveTasks
                .Select(task => task.Task)
                .ToList();
        }
    }

    public ChecklistTaskData? GetNextActiveGeneralInteractTask(long matchingId, long playerId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return null;

        lock (state.SyncRoot)
        {
            return state.GetOrCreatePlayerState(playerId)
                .ActiveTasks
                .Select(task => task.Task)
                .FirstOrDefault(task =>
                    task.Category == ChecklistTaskCategory.GeneralJob &&
                    task.CompletionEvent.Equals("interact_object", StringComparison.OrdinalIgnoreCase) &&
                    task.InteractId > 0 &&
                    task.AreaType > 0);
        }
    }

    public bool TryCompleteTargetGiftTask(long matchingId, long playerId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return false;

        lock (state.SyncRoot)
        {
            var playerState = state.GetOrCreatePlayerState(playerId);
            var activeTask = playerState.ActiveTasks.FirstOrDefault(task =>
                task.Task.TaskKey.Equals("MANITTO_TARGET_DISCOVERS_GIFT", StringComparison.OrdinalIgnoreCase));
            if (activeTask == null) return false;

            CompleteActiveTask(playerState, activeTask, consumedRequiredItem: false, consumedItemUpdate: null);
            return true;
        }
    }

    public List<ChecklistTaskProgressInfo> GetActiveTaskProgresses(long matchingId, long playerId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return new List<ChecklistTaskProgressInfo>();

        lock (state.SyncRoot)
        {
            return state.GetOrCreatePlayerState(playerId)
                .ActiveTasks
                .Select(task => new ChecklistTaskProgressInfo
                {
                    TaskId = task.Task.TaskId,
                    Progress = task.Progress01
                })
                .ToList();
        }
    }

    public List<int> GetCompletedTaskIds(long matchingId, long playerId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return new List<int>();

        lock (state.SyncRoot)
        {
            return state.GetOrCreatePlayerState(playerId)
                .CompletedTaskIds
                .OrderBy(id => id)
                .ToList();
        }
    }

    public ChecklistProgressAdvanceResult AdvanceActiveTaskProgress(
        long matchingId,
        long playerId,
        string taskKey,
        float deltaSeconds)
    {
        if (string.IsNullOrWhiteSpace(taskKey) || deltaSeconds <= 0f)
            return new ChecklistProgressAdvanceResult();

        if (!_states.TryGetValue(matchingId, out var state))
            return new ChecklistProgressAdvanceResult();

        lock (state.SyncRoot)
        {
            var playerState = state.GetOrCreatePlayerState(playerId);
            var activeTask = playerState.ActiveTasks.FirstOrDefault(task =>
                task.Task.TaskKey.Equals(taskKey, StringComparison.OrdinalIgnoreCase));
            if (activeTask == null || activeTask.Task.DurationSeconds <= 0)
                return new ChecklistProgressAdvanceResult();

            float before = activeTask.ProgressSeconds;
            activeTask.ProgressSeconds = Math.Min(activeTask.Task.DurationSeconds, before + deltaSeconds);
            bool changed = Math.Abs(before - activeTask.ProgressSeconds) > 0.0001f;
            if (!changed)
                return new ChecklistProgressAdvanceResult();

            if (activeTask.ProgressSeconds < activeTask.Task.DurationSeconds)
                return new ChecklistProgressAdvanceResult { Changed = true };

            return new ChecklistProgressAdvanceResult
            {
                Changed = true,
                Completion = CompleteActiveTask(playerState, activeTask, consumedRequiredItem: false,
                    consumedItemUpdate: null)
            };
        }
    }

    public ChecklistCompletionResult TryCompleteTask(
        long matchingId,
        long playerId,
        int taskId,
        AreaType currentArea,
        int interactId,
        InGameInventoryManager inventoryManager)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return new ChecklistCompletionResult { ErrorCode = ErrorCode.INVALID_GAME_STATE };

        lock (state.SyncRoot)
        {
            var playerState = state.GetOrCreatePlayerState(playerId);
            var activeTask = playerState.ActiveTasks.FirstOrDefault(task => task.Task.TaskId == taskId);
            if (activeTask == null)
                return new ChecklistCompletionResult { ErrorCode = ErrorCode.INVALID_SELECTION };

            var task = activeTask.Task;
            if (task.AreaType > 0 && task.AreaType != (int)currentArea)
                return new ChecklistCompletionResult { ErrorCode = ErrorCode.INVALID_AREA };

            if (task.InteractId > 0 && interactId > 0 && task.InteractId != interactId)
                return new ChecklistCompletionResult { ErrorCode = ErrorCode.INVALID_SELECTION };

            if (EnableRequiredItemChecks &&
                task.RequiredItemId > 0 &&
                !HasRequiredItem(matchingId, playerId, task.RequiredItemId, inventoryManager))
                return new ChecklistCompletionResult { ErrorCode = ErrorCode.INSUFFICIENT_ITEM };

            bool consumed = false;
            InGameItemInfo? consumedItemUpdate = null;
            if (EnableRequiredItemChecks && ShouldConsumeRequiredItem(task))
            {
                var removed = inventoryManager.TryRemoveOneByItemId(matchingId, playerId, task.RequiredItemId,
                    out consumedItemUpdate);
                if (!removed)
                    return new ChecklistCompletionResult { ErrorCode = ErrorCode.INSUFFICIENT_ITEM };

                consumed = true;
            }

            ChecklistCompletionResult result = CompleteActiveTask(playerState, activeTask, consumed,
                consumedItemUpdate);

            logger.LogInformation(
                "Checklist task completed: MatchingId={MatchingId}, PlayerId={PlayerId}, TaskId={TaskId}, Score={Score}, Consumed={Consumed}, NextTaskId={NextTaskId}",
                matchingId, playerId, task.TaskId, result.AwardedScore, consumed, result.NextGeneralJob?.TaskId ?? 0);

            return result;
        }
    }

    private ChecklistCompletionResult CompleteActiveTask(
        PlayerChecklistState playerState,
        ChecklistActiveTask activeTask,
        bool consumedRequiredItem,
        InGameItemInfo? consumedItemUpdate)
    {
        var task = activeTask.Task;
        int beforeContribution = CalculateContribution(playerState);
        playerState.ActiveTasks.Remove(activeTask);
        float awardedScore = AwardTaskScore(playerState, task);
        int afterContribution = CalculateContribution(playerState);
        playerState.CompletedTaskIds.Add(task.TaskId);
        RememberAntiFarmTags(playerState, task);

        ChecklistTaskData? nextGeneralJob = null;
        if (task.Category == ChecklistTaskCategory.GeneralJob && IsChainNext(task))
        {
            nextGeneralJob = PickGeneralJob(playerState);
            if (nextGeneralJob != null)
                playerState.ActiveTasks.Add(new ChecklistActiveTask(nextGeneralJob));
        }

        return new ChecklistCompletionResult
        {
            ErrorCode = ErrorCode.SUCCESS,
            CompletedTask = task,
            NextGeneralJob = nextGeneralJob,
            AwardedScore = awardedScore,
            AwardedContribution = Math.Max(0, afterContribution - beforeContribution),
            ConsumedRequiredItem = consumedRequiredItem,
            ConsumedItemUpdate = consumedItemUpdate
        };
    }

    private static int CalculateContribution(PlayerChecklistState playerState)
    {
        if (playerState == null) return 0;

        float totalScore = playerState.GeneralJobScore + playerState.ManittoRoleScore + playerState.BonusScore;
        return Math.Max(0, (int)MathF.Round(totalScore * ContributionScale, MidpointRounding.AwayFromZero));
    }

    public List<ChecklistPlayerContribution> GetPlayerContributions(long matchingId, IEnumerable<long> playerIds)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return playerIds.Distinct().Select(id => new ChecklistPlayerContribution { PlayerId = id }).ToList();

        lock (state.SyncRoot)
        {
            return playerIds
                .Distinct()
                .OrderBy(id => id)
                .Select(id =>
                {
                    var playerState = state.GetOrCreatePlayerState(id);
                    return new ChecklistPlayerContribution
                    {
                        PlayerId = id,
                        GeneralJobScore = playerState.GeneralJobScore,
                        ManittoRoleScore = playerState.ManittoRoleScore,
                        BonusScore = playerState.BonusScore
                    };
                })
                .ToList();
        }
    }

    public List<SettlementContributionEntry> BuildSettlementContributionEntries(long matchingId, IEnumerable<long> playerIds)
    {
        return GetPlayerContributions(matchingId, playerIds)
            .Select(contribution => new SettlementContributionEntry
            {
                PlayerId = contribution.PlayerId,
                Contribution = Math.Max(0, contribution.Contribution)
            })
            .ToList();
    }

    public void RemoveMatchingState(long matchingId)
    {
        if (_states.TryRemove(matchingId, out _))
            _logAction?.Invoke($"ChecklistManager: Removed state for MatchingId={matchingId}");
    }

    private void GenerateRoundStartTasks(PlayerChecklistState playerState, ChecklistChainContext chainContext)
    {
        var stateRule = GameChecklistData.GetStateRule(ResolveChainState(chainContext));
        if (stateRule == null) return;

        for (int i = 0; i < stateRule.GeneralJobCount; i++)
        {
            var generalJob = PickGeneralJob(playerState);
            if (generalJob == null) break;
            playerState.ActiveTasks.Add(new ChecklistActiveTask(generalJob));
        }

        int roleMin = Math.Max(_manittoRoleRoundLimitMin, stateRule.ManittoRoleMinCount);
        int roleMax = Math.Min(_manittoRoleRoundLimitMax, stateRule.ManittoRoleMaxCount);
        int roleCount = roleMax <= 0 ? 0 : Random.Shared.Next(roleMin, roleMax + 1);
        foreach (var role in PickManittoRoles(chainContext, roleCount))
            playerState.ActiveTasks.Add(new ChecklistActiveTask(role));
    }

    private ChecklistTaskData? PickGeneralJob(PlayerChecklistState playerState)
    {
        var candidates = GameChecklistData.GetGeneralJobs()
            .Where(task => !playerState.CompletedTaskIds.Contains(task.TaskId))
            .Where(task => !ConflictsWithRoundAntiFarm(playerState, task))
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = GameChecklistData.GetGeneralJobs()
                .Where(task => !playerState.CompletedTaskIds.Contains(task.TaskId))
                .ToList();
        }

        return PickRandom(candidates);
    }

    private static List<ChecklistTaskData> PickManittoRoles(ChecklistChainContext chainContext, int count)
    {
        if (count <= 0) return new List<ChecklistTaskData>();

        var candidates = GameChecklistData.GetManittoRoles()
            .Where(task => !task.RequiresTargetAlive || chainContext.TargetAlive)
            .Where(task => !task.RequiresTargetLost || !chainContext.TargetAlive)
            .Where(task => !task.RequiresManittoAlive || chainContext.ManittoAlive)
            .OrderBy(_ => Random.Shared.Next())
            .Take(count)
            .ToList();

        return candidates;
    }

    private static ChecklistTaskData? PickRandom(List<ChecklistTaskData> tasks)
    {
        if (tasks.Count == 0) return null;
        return tasks[Random.Shared.Next(tasks.Count)];
    }

    private static ChecklistChainState ResolveChainState(ChecklistChainContext context) =>
        (context.TargetAlive, context.ManittoAlive) switch
        {
            (true, true) => ChecklistChainState.Normal,
            (false, true) => ChecklistChainState.TargetLost,
            (true, false) => ChecklistChainState.ManittoLost,
            _ => ChecklistChainState.BothLost
        };

    private static bool HasRequiredItem(
        long matchingId,
        long playerId,
        int itemId,
        InGameInventoryManager inventoryManager)
    {
        return inventoryManager.GetAllItems(matchingId, playerId)
            .Any(item => item.ItemId == itemId && item.Count > 0);
    }

    private static bool ShouldConsumeRequiredItem(ChecklistTaskData task)
    {
        return task.RequiredItemId > 0
               && (task.ConsumeRequiredItem || task.RequiredItemPolicy == ChecklistRequiredItemPolicy.Consume);
    }

    private float AwardTaskScore(PlayerChecklistState playerState, ChecklistTaskData task)
    {
        if (task.Category == ChecklistTaskCategory.ManittoRole)
        {
            playerState.ManittoRoleScore += task.Score;
            return task.Score;
        }

        playerState.GeneralJobCompletionCount++;

        float remainingCap = Math.Max(0f, _generalRoundScoreCap - playerState.GeneralJobScore);
        float uncappedScore = playerState.GeneralJobCompletionCount <= _generalScoringLimitCount
            ? task.Score
            : _generalAfterLimitScore;
        float awarded = Math.Min(uncappedScore, remainingCap);
        playerState.GeneralJobScore += awarded;
        return awarded;
    }

    private static bool IsChainNext(ChecklistTaskData task) =>
        task.ChainPolicy.Equals("chain_on_complete", StringComparison.OrdinalIgnoreCase) ||
        task.ChainPolicy.Equals("next_on_complete", StringComparison.OrdinalIgnoreCase);

    private static void RememberAntiFarmTags(PlayerChecklistState playerState, ChecklistTaskData task)
    {
        foreach (string tag in task.AntiFarmTags)
        {
            if (tag.StartsWith("area:", StringComparison.OrdinalIgnoreCase))
                playerState.UsedAreaTags.Add(tag);
            else if (tag.StartsWith("action:", StringComparison.OrdinalIgnoreCase))
                playerState.UsedActionTags.Add(tag);
        }
    }

    private static bool ConflictsWithRoundAntiFarm(PlayerChecklistState playerState, ChecklistTaskData task)
    {
        foreach (string tag in task.AntiFarmTags)
        {
            if (tag.StartsWith("area:", StringComparison.OrdinalIgnoreCase)
                && playerState.UsedAreaTags.Contains(tag))
                return true;

            if (tag.StartsWith("action:", StringComparison.OrdinalIgnoreCase)
                && playerState.UsedActionTags.Contains(tag))
                return true;
        }

        return false;
    }
}

internal sealed class MatchingChecklistState(long matchingId)
{
    private readonly Dictionary<long, PlayerChecklistState> _players = new();

    public long MatchingId { get; } = matchingId;
    public int RoundNumber { get; set; }
    public object SyncRoot { get; } = new();

    public PlayerChecklistState GetOrCreatePlayerState(long playerId)
    {
        if (!_players.TryGetValue(playerId, out var state))
        {
            state = new PlayerChecklistState(playerId);
            _players[playerId] = state;
        }

        return state;
    }
}

internal sealed class PlayerChecklistState(long playerId)
{
    public long PlayerId { get; } = playerId;
    public int RoundNumber { get; private set; }
    public List<ChecklistActiveTask> ActiveTasks { get; } = new();
    public HashSet<int> CompletedTaskIds { get; } = new();
    public HashSet<string> UsedAreaTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UsedActionTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public float GeneralJobScore { get; set; }
    public float ManittoRoleScore { get; set; }
    public float BonusScore { get; set; }
    public int GeneralJobCompletionCount { get; set; }

    public void ResetForRound(int roundNumber)
    {
        RoundNumber = roundNumber;
        ActiveTasks.Clear();
        CompletedTaskIds.Clear();
        UsedAreaTags.Clear();
        UsedActionTags.Clear();
        GeneralJobScore = 0f;
        ManittoRoleScore = 0f;
        BonusScore = 0f;
        GeneralJobCompletionCount = 0;
    }
}

internal sealed class ChecklistActiveTask(ChecklistTaskData task)
{
    public ChecklistTaskData Task { get; } = task;
    public float ProgressSeconds { get; set; }
    public float Progress01 => Task.DurationSeconds > 0
        ? Math.Clamp(ProgressSeconds / Task.DurationSeconds, 0f, 1f)
        : 0f;
}
