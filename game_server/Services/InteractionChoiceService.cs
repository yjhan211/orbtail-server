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
    public const int DemoQuestionTextId = 11033;
    public const int DemoPassingAnswerTextId = 11034;
    public const int DemoMissionAnswerTextId = 11035;
    public const int DemoStaminaAnswerTextId = 11036;
    public const int DemoRecordAnswerTextId = 11037;

    public const string NearbyReasonQuestionId = "ASK_NEARBY_REASON";
    public const string EnRouteAnswerType = "EN_ROUTE";
    public const string ActivityInAreaAnswerType = "ACTIVITY_IN_AREA";
    public const string EnRouteToAreaAnswerType = "EN_ROUTE_TO_AREA";
    public const string CoincidenceAnswerType = "COINCIDENCE";
    public const int NearbyReasonQuestionTextId = 11045;
    public const int EnRouteAnswerTextId = 11046;
    public const int CoincidenceAnswerTextId = 11047;
    public const int ActivityInAreaAnswerTextId = 11048;
    public const int EnRouteToAreaAnswerTextId = 11049;
    public const string NearbyReasonQuestionText = "여기엔 무슨 일로 왔나요?";
    public const string EnRouteAnswerText = "이동 중이었습니다.";
    public const string CoincidenceAnswerText = "우연입니다.";

    private const int NearbyReasonRecentWindowSeconds = 20;
    private const int ActivityEvidenceRecentWindowSeconds = 60;

    private readonly InteractionLogManager _logManager;
    private readonly ManittoChainManager _chainManager;
    private readonly GameEventLogManager? _eventLogManager;

    public InteractionChoiceService(
        InteractionLogManager logManager,
        ManittoChainManager chainManager,
        GameEventLogManager? eventLogManager = null)
    {
        _logManager = logManager;
        _chainManager = chainManager;
        _eventLogManager = eventLogManager;
    }

    public List<InteractionQuestion> GenerateQuestions(
        long matchingId,
        long askerPlayerId,
        long answererPlayerId,
        AreaType currentArea,
        AreaType? answererPreviousArea) =>
        GenerateQuestionSet(matchingId, askerPlayerId, answererPlayerId, currentArea, answererPreviousArea).Questions;

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

    public List<InteractionQuestion> GenerateDemoQuestions(AreaType currentArea)
    {
        return BuildLocationQuestionList(currentArea, DemoQuestionTextId);
    }

    private List<InteractionQuestion> BuildStandardQuestionList(
        long matchingId,
        long askerPlayerId,
        long answererPlayerId,
        AreaType currentArea)
    {
        var questions = new List<InteractionQuestion>
        {
            new()
            {
                QuestionType = InteractionQuestionType.ASK_LOCATION,
                TextId = 11020,
                Args = new List<TextArg> { new() { Type = TextArgType.AREA_TYPE, IntValue = (int)currentArea } },
                ReferenceArea = currentArea
            },
            new()
            {
                QuestionType = InteractionQuestionType.ASK_JOB,
                TextId = 11021
            }
        };

        var askerLogs = _logManager.GetLogs(matchingId, askerPlayerId);
        var answererPreviousClaim = askerLogs
            .Where(l => l.OtherPlayerId == answererPlayerId)
            .OrderByDescending(l => l.Timestamp)
            .FirstOrDefault();

        if (answererPreviousClaim != null)
        {
            var sameClaim = askerLogs
                .FirstOrDefault(l => l.OtherPlayerId != answererPlayerId &&
                                     l.ClaimedJobTitle == answererPreviousClaim.ClaimedJobTitle);

            var jobArg = new TextArg
            {
                Type = TextArgType.JOB_TITLE,
                IntValue = (int)answererPreviousClaim.ClaimedJobTitle
            };

            questions.Add(new InteractionQuestion
            {
                QuestionType = InteractionQuestionType.CROSS_CHECK,
                TextId = sameClaim != null ? 11022 : 11023,
                Args = new List<TextArg> { jobArg },
                ReferencePlayerId = sameClaim?.OtherPlayerId ?? 0
            });
        }

        questions.Add(new InteractionQuestion
        {
            QuestionType = InteractionQuestionType.ASK_TRACE,
            TextId = 11024
        });

        return questions;
    }

    private static List<InteractionQuestion> BuildLocationQuestionList(AreaType currentArea, int textId = 11020)
    {
        return new List<InteractionQuestion>
        {
            new()
            {
                QuestionType = InteractionQuestionType.ASK_LOCATION,
                TextId = textId,
                Args = new List<TextArg> { new() { Type = TextArgType.AREA_TYPE, IntValue = (int)currentArea } },
                ReferenceArea = currentArea
            }
        };
    }

    public List<InteractionAnswer> GenerateAnswers(
        long matchingId,
        long answererPlayerId,
        InteractionQuestionType questionType,
        AreaType currentArea) =>
        GenerateAnswerSet(matchingId, answererPlayerId, 0, questionType, currentArea, null).Answers;

    public InteractionAnswerSet GenerateAnswerSet(
        long matchingId,
        long answererPlayerId,
        long askerPlayerId,
        InteractionQuestionType questionType,
        AreaType currentArea,
        InteractionQuestionContext? questionContext,
        AreaType? answererDestinationArea = null)
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
                 ((entry.Type == "ENCOUNTER" && entry.EncounteredPlayerIds?.Contains(askerPlayerId) == true)
                  || (entry.Type == "FOLLOW_IN_CANDIDATE" && entry.RecentPlayerIds?.Contains(askerPlayerId) == true)))
                || (entry.ActorPlayerId == askerPlayerId &&
                    ((entry.Type == "ENCOUNTER" && entry.EncounteredPlayerIds?.Contains(answererPlayerId) == true)
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

    private static TextArg CreateAreaLootItemArg(AreaType currentArea)
    {
        var areaLootItemId = GameInteractableData.GetItemPoolByArea((int)currentArea)
            .FirstOrDefault(itemId => GameItemData.Get(itemId) != null);

        return areaLootItemId > 0
            ? new TextArg { Type = TextArgType.ITEM_NAME, IntValue = areaLootItemId }
            : new TextArg { Type = TextArgType.RAW_STRING, StringValue = "단서" };
    }

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
