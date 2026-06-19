using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;

namespace game_server.services;

/// <summary>
///     1:1 상호작용 시 질문/답변 선택지 동적 생성.
///     GDD 2.4 기반: 활동 로그 기반 동적 생성, 교차 검증, 사칭 발각.
///     모든 텍스트는 textId + TextArg 스킴으로 직렬화 — 클라가 현재 언어로 변환.
/// </summary>
public class InteractionChoiceService
{
    public const int DemoQuestionTextId = 11033;
    public const int DemoPassingAnswerTextId = 11034;
    public const int DemoMissionAnswerTextId = 11035;
    public const int DemoStaminaAnswerTextId = 11036;
    public const int DemoRecordAnswerTextId = 11037;

    private readonly InteractionLogManager _logManager;
    private readonly ManittoChainManager _chainManager;

    public InteractionChoiceService(InteractionLogManager logManager, ManittoChainManager chainManager)
    {
        _logManager = logManager;
        _chainManager = chainManager;
    }

    /// <summary>
    ///     질문 선택지 생성 (질문자 기준)
    ///     카테고리: 직책추궁(항상), 동선추궁(상대 로그 기반), 교차검증(이전 로그), 흔적추궁(구역 흔적)
    /// </summary>
    public List<InteractionQuestion> GenerateQuestions(
        long matchingId,
        long askerPlayerId,
        long answererPlayerId,
        AreaType currentArea,
        AreaType? answererPreviousArea)
    {
        if (DemoMode.IsActive) return GenerateDemoQuestions(currentArea);

        var p0Questions = BuildLocationQuestionList(currentArea);
        if (p0Questions.Count > 0) return p0Questions;

        var questions = new List<InteractionQuestion>();

        // 1. 만남 장소 추궁 (항상 포함)
        questions.Add(new InteractionQuestion
        {
            QuestionType = InteractionQuestionType.ASK_LOCATION,
            TextId = 11020,
            Args = new List<TextArg> { new() { Type = TextArgType.AREA_TYPE, IntValue = (int)currentArea } },
            ReferenceArea = currentArea
        });

        // 2. 직책 추궁 (항상 포함)
        questions.Add(new InteractionQuestion
        {
            QuestionType = InteractionQuestionType.ASK_JOB,
            TextId = 11021
        });

        // 3. 교차 검증 (이전 조우에서 상대가 주장한 직책과 충돌 가능성)
        var askerLogs = _logManager.GetLogs(matchingId, askerPlayerId);
        var answererPreviousClaim = askerLogs
            .Where(l => l.OtherPlayerId == answererPlayerId)
            .OrderByDescending(l => l.Timestamp)
            .FirstOrDefault();

        if (answererPreviousClaim != null)
        {
            var sameClaim = askerLogs
                .Where(l => l.OtherPlayerId != answererPlayerId &&
                            l.ClaimedJobTitle == answererPreviousClaim.ClaimedJobTitle)
                .FirstOrDefault();

            var jobArg = new TextArg { Type = TextArgType.JOB_TITLE, IntValue = (int)answererPreviousClaim.ClaimedJobTitle };
            if (sameClaim != null)
            {
                questions.Add(new InteractionQuestion
                {
                    QuestionType = InteractionQuestionType.CROSS_CHECK,
                    TextId = 11022,
                    Args = new List<TextArg> { jobArg },
                    ReferencePlayerId = sameClaim.OtherPlayerId
                });
            }
            else
            {
                questions.Add(new InteractionQuestion
                {
                    QuestionType = InteractionQuestionType.CROSS_CHECK,
                    TextId = 11023,
                    Args = new List<TextArg> { jobArg }
                });
            }
        }

        // 4. 흔적 추궁
        questions.Add(new InteractionQuestion
        {
            QuestionType = InteractionQuestionType.ASK_TRACE,
            TextId = 11024
        });

        return questions;
    }

    public List<InteractionQuestion> GenerateDemoQuestions(AreaType currentArea)
    {
        return BuildLocationQuestionList(currentArea, DemoQuestionTextId);
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

    /// <summary>
    ///     답변 선택지 생성 (답변자 기준)
    ///     진실: 실제 직책 / 거짓: 시스템이 제안하는 가짜 직책
    /// </summary>
    public List<InteractionAnswer> GenerateAnswers(
        long matchingId,
        long answererPlayerId,
        InteractionQuestionType questionType)
    {
        var answers = new List<InteractionAnswer>();
        var link = _chainManager.GetLink(matchingId, answererPlayerId);
        if (link == null) return answers;

        JobTitle realJob = link.MyJobTitle;

        // 1. 직책 응답 (50% 확률로 진실 또는 사칭)
        bool tellTruth = Random.Shared.Next(2) == 0;
        JobTitle claimedJob = tellTruth
            ? realJob
            : Enum.GetValues<JobTitle>()
                .Where(j => j != JobTitle.NONE && j != realJob)
                .OrderBy(_ => Random.Shared.Next())
                .First();
        answers.Add(new InteractionAnswer
        {
            IsTrue = tellTruth,
            ClaimedJob = claimedJob,
            TextId = 11030,
            Args = new List<TextArg> { new() { Type = TextArgType.JOB_TITLE, IntValue = (int)claimedJob } }
        });

        // 2. 알리바이
        answers.Add(new InteractionAnswer
        {
            IsTrue = false,
            ClaimedJob = JobTitle.NONE,
            TextId = 11031
        });

        if (DemoMode.IsActive) return answers;

        // 3. 자백
        answers.Add(new InteractionAnswer
        {
            IsTrue = false,
            ClaimedJob = JobTitle.NONE,
            TextId = 11032
        });

        return answers;
    }

    /// <summary>
    ///     답변 처리: 로그 기록 + 사칭 발각 체크
    ///     반환: (사칭 발각 여부, 충돌 정보 textId — 0이면 없음, 충돌 args)
    /// </summary>
    public (bool isFakeDetected, int conflictTextId, List<TextArg> conflictArgs) ProcessAnswer(
        long matchingId,
        long askerPlayerId,
        long answererPlayerId,
        JobTitle claimedJob,
        AreaType area,
        bool isTruthful)
    {
        // 로그 기록 (질문자 관점: 상대가 이 직책을 주장했다)
        _logManager.AddLog(matchingId, askerPlayerId, answererPlayerId, claimedJob, area);

        // 사칭 발각 체크: 같은 직책을 주장하는 다른 플레이어가 있는지
        var duplicates = _logManager.DetectDuplicateClaims(matchingId);
        var conflict = duplicates.FirstOrDefault(d => d.job == claimedJob && d.claimers.Count > 1);
        if (conflict.claimers != null && conflict.claimers.Contains(answererPlayerId))
        {
            var args = new List<TextArg> { new() { Type = TextArgType.JOB_TITLE, IntValue = (int)claimedJob } };
            return (true, 11042, args);
        }

        return (false, 0, null);
    }
}
