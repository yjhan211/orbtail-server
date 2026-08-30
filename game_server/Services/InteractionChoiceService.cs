using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;

namespace game_server.services;

public sealed class InteractionQuestionSet
{
    public List<InteractionQuestion> Questions { get; init; } = new();
    public List<InteractionQuestionContext> Contexts { get; init; } = new();
}

public sealed class InteractionQuestionContext
{
    public InteractionQuestionType QuestionType { get; init; }
    public string QuestionId { get; init; } = "";
    public string QuestionText { get; init; } = "";
    public AreaType Area { get; init; }
    public List<long> LinkedLogIds { get; init; } = new();
}

public sealed class InteractionAnswerSet
{
    public List<InteractionAnswer> Answers { get; init; } = new();
    public List<InteractionAnswerContext> Contexts { get; init; } = new();
}

public sealed class InteractionAnswerContext
{
    public string QuestionId { get; init; } = "";
    public string QuestionText { get; init; } = "";
    public string AnswerType { get; init; } = "";
    public string AnswerText { get; init; } = "";
    public AreaType Area { get; init; }
    public List<long> LinkedLogIds { get; init; } = new();
}

/// <summary>
///     Generates one-on-one interaction questions and answers.
/// </summary>
public class InteractionChoiceService
{
    public const string NearbyReasonQuestionId = "ASK_NEARBY_REASON";
    public const string EncounterActionQuestionId = "ENCOUNTER_ACTION";
    public const string ActivityInAreaAnswerType = "ACTIVITY_IN_AREA";
    public const string EnRouteToAreaAnswerType = "EN_ROUTE_TO_AREA";
    public const string CoincidenceAnswerType = "COINCIDENCE";
    public const string EncounterUseItemAnswerType = "USE_ITEM";
    public const string EncounterKeepDistanceAnswerType = "KEEP_DISTANCE";
    public const string EncounterLeaveAreaAnswerType = "LEAVE_AREA";
    public const int NearbyReasonQuestionTextId = 11045;
    public const int CoincidenceAnswerTextId = 11047;
    public const int ActivityInAreaAnswerTextId = 11048;
    public const int EnRouteToAreaAnswerTextId = 11049;
    public const int EncounterActionQuestionTextId = 11060;
    public const int EncounterUseItemAnswerTextId = 11061;
    public const int EncounterKeepDistanceAnswerTextId = 11062;
    public const int EncounterLeaveAreaAnswerTextId = 11064;
    public const string EncounterActionQuestionText = "어떻게 대응할까요?";
    public const string EncounterKeepDistanceAnswerText = "\uC0C1\uB300\uBC29\uC758 \uD589\uB3D9\uC5D0 \uB300\uBE44\uD558\uAE30";
    public const string EncounterLeaveAreaAnswerText = "\uC7A5\uC18C \uC774\uD0C8\uD558\uAE30 (\uC2A4\uD0DC\uBBF8\uB098 -5)";
    public const string NearbyReasonQuestionText = "여기엔 무슨 일로 왔나요?";
    public const string CoincidenceAnswerText = "우연입니다.";

    private const int NearbyReasonRecentWindowSeconds = 20;
    private const int ActivityEvidenceRecentWindowSeconds = 60;
    private const int EncounterChalkPowderItemId = 201000015;
    private const int EncounterShortChalkItemId = 201000016;
    private const int EncounterLongChalkItemId = 201000017;
    private static readonly int[] EncounterAttackItemIds =
    {
        EncounterLongChalkItemId,
        EncounterChalkPowderItemId,
        EncounterShortChalkItemId
    };

    private readonly InteractionLogManager _logManager;
    private readonly MatchRosterManager _rosterManager;
    private readonly GameEventLogManager? _eventLogManager;

    public InteractionChoiceService(
        InteractionLogManager logManager,
        MatchRosterManager rosterManager,
        GameEventLogManager? eventLogManager = null)
    {
        _logManager = logManager;
        _rosterManager = rosterManager;
        _eventLogManager = eventLogManager;
    }

    private static bool IsEncounterAttackItem(int itemId)
    {
        return Array.IndexOf(EncounterAttackItemIds, itemId) >= 0;
    }

    private static int GetEncounterAttackItemPriority(int itemId)
    {
        int index = Array.IndexOf(EncounterAttackItemIds, itemId);
        return index >= 0 ? index : int.MaxValue;
    }

    public InteractionQuestionSet GenerateQuestionSet(
        long matchingId,
        long askerPlayerId,
        long answererPlayerId,
        AreaType currentArea,
        AreaType? answererPreviousArea)
    {
        var nearbyReason = TryBuildNearbyReasonContext(matchingId, askerPlayerId, answererPlayerId, currentArea);
        if (nearbyReason != null)
        {
            return new InteractionQuestionSet
            {
                Questions = new List<InteractionQuestion>
                {
                    new()
                    {
                        QuestionType = InteractionQuestionType.ASK_NEARBY_REASON,
                        TextId = NearbyReasonQuestionTextId,
                        ReferenceArea = currentArea
                    }
                },
                Contexts = new List<InteractionQuestionContext> { nearbyReason }
            };
        }

        return new InteractionQuestionSet();
    }

    public InteractionAnswerSet GenerateEncounterActionAnswerSet(
        AreaType currentArea,
        IEnumerable<InGameItemInfo>? inventoryItems = null,
        IEnumerable<long>? linkedLogIds = null)
    {
        var answers = new List<InteractionAnswer>();
        var contexts = new List<InteractionAnswerContext>();
        var linked = linkedLogIds?.Distinct().ToList() ?? new List<long>();

        void AddAnswer(int textId, List<TextArg>? args, string answerType, string answerText)
        {
            answers.Add(new InteractionAnswer
            {
                IsTrue = false,
                ClaimedJob = JobTitle.NONE,
                TextId = textId,
                Args = args ?? new List<TextArg>()
            });

            contexts.Add(new InteractionAnswerContext
            {
                QuestionId = EncounterActionQuestionId,
                QuestionText = EncounterActionQuestionText,
                AnswerType = answerType,
                AnswerText = answerText,
                Area = currentArea,
                LinkedLogIds = linked.ToList()
            });
        }

        var usableItems = (inventoryItems ?? Enumerable.Empty<InGameItemInfo>())
            .Where(item => item.Count > 0 && IsEncounterAttackItem(item.ItemId))
            .GroupBy(item => item.ItemId)
            .Select(group => group.First())
            .OrderBy(item => GetEncounterAttackItemPriority(item.ItemId))
            .Take(1)
            .ToList();

        foreach (var item in usableItems)
        {
            string itemName = ResolveItemNameKr(item.ItemId);
            AddAnswer(
                EncounterUseItemAnswerTextId,
                new List<TextArg> { new() { Type = TextArgType.ITEM_NAME, IntValue = item.ItemId } },
                $"{EncounterUseItemAnswerType}:{item.ItemId}",
                $"\uC544\uC774\uD15C \uC0AC\uC6A9 [- {itemName}]");
        }

        AddAnswer(EncounterKeepDistanceAnswerTextId, null, EncounterKeepDistanceAnswerType,
            EncounterKeepDistanceAnswerText);
        AddAnswer(EncounterLeaveAreaAnswerTextId, null, EncounterLeaveAreaAnswerType,
            EncounterLeaveAreaAnswerText);

        return new InteractionAnswerSet
        {
            Answers = answers,
            Contexts = contexts
        };
    }

    public InteractionAnswerSet GenerateAnswerSet(
        long matchingId,
        long answererPlayerId,
        long askerPlayerId,
        InteractionQuestionType questionType,
        AreaType currentArea,
        InteractionQuestionContext? questionContext,
        AreaType? answererDestinationArea = null,
        int answererDestinationTaskId = 0)
    {
        if (questionType == InteractionQuestionType.ASK_NEARBY_REASON)
        {
            var context = questionContext
                          ?? TryBuildNearbyReasonContext(matchingId, askerPlayerId, answererPlayerId, currentArea)
                          ?? new InteractionQuestionContext
                          {
                              QuestionType = InteractionQuestionType.ASK_NEARBY_REASON,
                              QuestionId = NearbyReasonQuestionId,
                              QuestionText = NearbyReasonQuestionText,
                              Area = currentArea
                          };

            var nearbyAnswers = new List<InteractionAnswer>();
            var answerContexts = new List<InteractionAnswerContext>();

            var evidenceAnswer = TryBuildActivityAnswer(
                                     matchingId,
                                     answererPlayerId,
                                     currentArea,
                                     context)
                                 ?? TryBuildCurrentAreaDestinationActivityAnswer(
                                     answererDestinationArea,
                                     answererDestinationTaskId,
                                     currentArea,
                                     context)
                                 ?? TryBuildDestinationAnswer(
                                     answererDestinationArea,
                                     currentArea,
                                     context);

            if (evidenceAnswer != null)
            {
                nearbyAnswers.Add(evidenceAnswer.Answer);
                answerContexts.Add(evidenceAnswer.Context);
            }

            nearbyAnswers.Add(new InteractionAnswer
            {
                IsTrue = false,
                ClaimedJob = JobTitle.NONE,
                TextId = CoincidenceAnswerTextId
            });
            answerContexts.Add(new InteractionAnswerContext
            {
                QuestionId = NearbyReasonQuestionId,
                QuestionText = context.QuestionText,
                AnswerType = CoincidenceAnswerType,
                AnswerText = CoincidenceAnswerText,
                Area = context.Area,
                LinkedLogIds = context.LinkedLogIds.ToList()
            });

            return new InteractionAnswerSet
            {
                Answers = nearbyAnswers,
                Contexts = answerContexts
            };
        }

        return new InteractionAnswerSet();
    }

    private sealed class EvidenceAnswer
    {
        public InteractionAnswer Answer { get; init; } = new();
        public InteractionAnswerContext Context { get; init; } = new();
    }

    private EvidenceAnswer? TryBuildActivityAnswer(
        long matchingId,
        long answererPlayerId,
        AreaType currentArea,
        InteractionQuestionContext context)
    {
        if (_eventLogManager == null) return null;

        string areaName = currentArea.ToString();
        long cutoffUnixMs = DateTimeOffset.UtcNow
            .AddSeconds(-ActivityEvidenceRecentWindowSeconds)
            .ToUnixTimeMilliseconds();

        var activityLog = _eventLogManager.GetRecent(matchingId, 200)
            .Where(entry => entry.TimestampUnixMs >= cutoffUnixMs)
            .Where(entry => entry.ActorPlayerId == answererPlayerId)
            .Where(entry => string.Equals(entry.Area, areaName, StringComparison.Ordinal))
            .Where(entry => entry.Type == "SCHOOL_ACTIVITY_START" ||
                            entry.Type == "SCHOOL_ACTIVITY_COMPLETE")
            .OrderByDescending(entry => entry.TimestampUnixMs)
            .FirstOrDefault();

        if (activityLog == null) return null;

        string activityName = ResolveActivityName(activityLog);
        var linkedLogIds = MergeLinkedLogIds(context.LinkedLogIds, activityLog);

        return new EvidenceAnswer
        {
            Answer = new InteractionAnswer
            {
                IsTrue = false,
                ClaimedJob = JobTitle.NONE,
                TextId = ActivityInAreaAnswerTextId,
                Args = new List<TextArg> { new() { Type = TextArgType.RAW_STRING, StringValue = activityName } }
            },
            Context = new InteractionAnswerContext
            {
                QuestionId = NearbyReasonQuestionId,
                QuestionText = context.QuestionText,
                AnswerType = ActivityInAreaAnswerType,
                AnswerText = $"{activityName} 중이었습니다.",
                Area = context.Area,
                LinkedLogIds = linkedLogIds
            }
        };
    }

    private static EvidenceAnswer? TryBuildCurrentAreaDestinationActivityAnswer(
        AreaType? answererDestinationArea,
        int answererDestinationTaskId,
        AreaType currentArea,
        InteractionQuestionContext context)
    {
        if (!answererDestinationArea.HasValue || answererDestinationArea.Value != currentArea)
            return null;
        if (answererDestinationTaskId <= 0)
            return null;

        var task = GameChecklistData.GetTask(answererDestinationTaskId);
        if (task == null)
            return null;

        string activityName = ResolveActivityName(task);
        return new EvidenceAnswer
        {
            Answer = new InteractionAnswer
            {
                IsTrue = false,
                ClaimedJob = JobTitle.NONE,
                TextId = ActivityInAreaAnswerTextId,
                Args = new List<TextArg> { new() { Type = TextArgType.RAW_STRING, StringValue = activityName } }
            },
            Context = new InteractionAnswerContext
            {
                QuestionId = NearbyReasonQuestionId,
                QuestionText = context.QuestionText,
                AnswerType = ActivityInAreaAnswerType,
                AnswerText = $"{activityName} 중이었습니다.",
                Area = context.Area,
                LinkedLogIds = context.LinkedLogIds.ToList()
            }
        };
    }

    private static EvidenceAnswer? TryBuildDestinationAnswer(
        AreaType? answererDestinationArea,
        AreaType currentArea,
        InteractionQuestionContext context)
    {
        if (!answererDestinationArea.HasValue || answererDestinationArea.Value == AreaType.None)
            return null;

        AreaType destination = answererDestinationArea.Value;
        string destinationName = GameAreaNameData.Get(destination);

        return new EvidenceAnswer
        {
            Answer = new InteractionAnswer
            {
                IsTrue = false,
                ClaimedJob = JobTitle.NONE,
                TextId = EnRouteToAreaAnswerTextId,
                Args = new List<TextArg> { new() { Type = TextArgType.AREA_TYPE, IntValue = (int)destination } }
            },
            Context = new InteractionAnswerContext
            {
                QuestionId = NearbyReasonQuestionId,
                QuestionText = context.QuestionText,
                AnswerType = EnRouteToAreaAnswerType,
                AnswerText = $"{destinationName}(으)로 이동 중이었습니다.",
                Area = context.Area,
                LinkedLogIds = context.LinkedLogIds.ToList()
            }
        };
    }

    private static string ResolveActivityName(GameEventEntry activityLog)
    {
        if (!string.IsNullOrWhiteSpace(activityLog.ActivityReason))
            return activityLog.ActivityReason.Trim();

        if (activityLog.TaskId.HasValue)
        {
            var task = GameChecklistData.GetTask(activityLog.TaskId.Value);
            if (!string.IsNullOrWhiteSpace(task?.TitleKr))
                return task.TitleKr.Trim();
        }

        return "교내 활동";
    }

    private static string ResolveActivityName(ChecklistTaskData task)
    {
        if (!string.IsNullOrWhiteSpace(task.TitleKr))
            return task.TitleKr.Trim();

        return "교내 활동";
    }

    private static string ResolveItemNameKr(int itemId)
    {
        var item = GameItemData.Get(itemId);
        string name = item?.Name?.Kr ?? "";
        return !string.IsNullOrWhiteSpace(name) ? name.Trim() : $"Item{itemId}";
    }

    private static List<long> MergeLinkedLogIds(IEnumerable<long> baseLogIds, GameEventEntry extraLog)
    {
        var linkedLogIds = new List<long>();
        linkedLogIds.AddRange(baseLogIds);
        if (extraLog.SourceEventSeq.HasValue) linkedLogIds.Add(extraLog.SourceEventSeq.Value);
        linkedLogIds.Add(extraLog.Seq);
        return linkedLogIds.Distinct().ToList();
    }

    private InteractionQuestionContext? TryBuildNearbyReasonContext(
        long matchingId,
        long askerPlayerId,
        long answererPlayerId,
        AreaType currentArea)
    {
        if (askerPlayerId == 0 || answererPlayerId == 0 || askerPlayerId == answererPlayerId) return null;
        if (currentArea == AreaType.None) return null;
        if (_eventLogManager == null)
            return null;

        string areaName = currentArea.ToString();
        long cutoffUnixMs = DateTimeOffset.UtcNow
            .AddSeconds(-NearbyReasonRecentWindowSeconds)
            .ToUnixTimeMilliseconds();

        var recentAreaLogs = _eventLogManager.GetRecent(matchingId, 200)
            .Where(entry => entry.TimestampUnixMs >= cutoffUnixMs)
            .Where(entry => string.Equals(entry.Area, areaName, StringComparison.Ordinal))
            .ToList();

        var relevantLogs = recentAreaLogs
            .Where(entry =>
                (entry.ActorPlayerId == answererPlayerId &&
                 ((IsEncounterEvidenceLog(entry) && entry.EncounteredPlayerIds?.Contains(askerPlayerId) == true)
                  || (entry.Type == "FOLLOW_IN_CANDIDATE" && entry.RecentPlayerIds?.Contains(askerPlayerId) == true)))
                || (entry.ActorPlayerId == askerPlayerId &&
                    ((IsEncounterEvidenceLog(entry) && entry.EncounteredPlayerIds?.Contains(answererPlayerId) == true)
                     || (entry.Type == "FOLLOW_IN_CANDIDATE" &&
                         entry.RecentPlayerIds?.Contains(answererPlayerId) == true))))
            .OrderBy(entry => entry.TimestampUnixMs)
            .ToList();

        if (relevantLogs.Count == 0)
            return null;

        var linkedLogIds = new List<long>();
        foreach (var log in relevantLogs)
        {
            if (log.SourceEventSeq.HasValue) linkedLogIds.Add(log.SourceEventSeq.Value);
            linkedLogIds.Add(log.Seq);
        }

        return new InteractionQuestionContext
        {
            QuestionType = InteractionQuestionType.ASK_NEARBY_REASON,
            QuestionId = NearbyReasonQuestionId,
            QuestionText = NearbyReasonQuestionText,
            Area = currentArea,
            LinkedLogIds = linkedLogIds.Distinct().ToList()
        };
    }

    private static bool IsEncounterEvidenceLog(GameEventEntry entry) =>
        entry.Type == "ENCOUNTER" || entry.Type == "ROOM_ENCOUNTER_REVEAL";

    public (bool isFakeDetected, int conflictTextId, List<TextArg> conflictArgs) ProcessAnswer(
        long matchingId,
        long askerPlayerId,
        long answererPlayerId,
        JobTitle claimedJob,
        AreaType area,
        bool isTruthful)
    {
        _logManager.AddLog(matchingId, askerPlayerId, answererPlayerId, claimedJob, area);

        if (claimedJob == JobTitle.NONE)
            return (false, 0, new List<TextArg>());

        var duplicates = _logManager.DetectDuplicateClaims(matchingId);
        var conflict = duplicates.FirstOrDefault(d => d.job == claimedJob && d.claimers.Count > 1);
        if (conflict.claimers != null && conflict.claimers.Contains(answererPlayerId))
        {
            var args = new List<TextArg> { new() { Type = TextArgType.JOB_TITLE, IntValue = (int)claimedJob } };
            return (true, 11042, args);
        }

        return (false, 0, new List<TextArg>());
    }
}
