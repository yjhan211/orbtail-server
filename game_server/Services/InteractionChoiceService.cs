using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     1:1 상호작용 시 질문/답변 선택지 동적 생성.
///     GDD 2.4 기반: 활동 로그 기반 동적 생성, 교차 검증, 사칭 발각.
/// </summary>
public class InteractionChoiceService
{
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
        var questions = new List<InteractionQuestion>();
        string currentAreaName = GameAreaNameData.Get(currentArea);

        // 1. 만남 장소 추궁 (항상 포함)
        questions.Add(new InteractionQuestion
        {
            QuestionType = InteractionQuestionType.ASK_LOCATION,
            Text = $"{currentAreaName}으로 온 이유가 궁금합니다.",
            ReferenceArea = currentArea
        });

        // 2. 직책 추궁 (항상 포함)
        questions.Add(new InteractionQuestion
        {
            QuestionType = InteractionQuestionType.ASK_JOB,
            Text = "직책이 무엇인지 궁금합니다."
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

            string prevJob = answererPreviousClaim.ClaimedJobTitle.ToKorean();
            if (sameClaim != null)
            {
                questions.Add(new InteractionQuestion
                {
                    QuestionType = InteractionQuestionType.CROSS_CHECK,
                    Text = $"다른 분도 {prevJob}이라고 주장하시던데, 사실인가요?",
                    ReferencePlayerId = sameClaim.OtherPlayerId
                });
            }
            else
            {
                questions.Add(new InteractionQuestion
                {
                    QuestionType = InteractionQuestionType.CROSS_CHECK,
                    Text = $"저번에 {prevJob}이라고 하셨는데, 정말 그러신가요?"
                });
            }
        }

        // 4. 흔적 추궁
        questions.Add(new InteractionQuestion
        {
            QuestionType = InteractionQuestionType.ASK_TRACE,
            Text = "여기서 무엇을 보셨는지 궁금합니다."
        });

        return questions;
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
            Text = $"{claimedJob.ToKorean()} 미션을 수행하러 왔습니다."
        });

        // 2. 알리바이
        answers.Add(new InteractionAnswer
        {
            IsTrue = false,
            ClaimedJob = JobTitle.NONE,
            Text = "구역 폐쇄로 인해 지나가던 도중입니다."
        });

        // 3. 자백
        answers.Add(new InteractionAnswer
        {
            IsTrue = false,
            ClaimedJob = JobTitle.NONE,
            Text = "저는 당신의 마니또입니다."
        });

        return answers;
    }

    /// <summary>
    ///     답변 처리: 로그 기록 + 사칭 발각 체크
    ///     반환: (사칭 발각 여부, 충돌 정보 텍스트)
    /// </summary>
    public (bool isFakeDetected, string conflictInfo) ProcessAnswer(
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
        bool isFakeDetected = false;
        string conflictInfo = "";

        var duplicates = _logManager.DetectDuplicateClaims(matchingId);
        var conflict = duplicates.FirstOrDefault(d => d.job == claimedJob && d.claimers.Count > 1);
        if (conflict.claimers != null && conflict.claimers.Contains(answererPlayerId))
        {
            isFakeDetected = true;
            conflictInfo = $"{claimedJob.ToKorean()}을(를) 주장하는 사람이 여러 명 발견되었습니다!";
        }

        return (isFakeDetected, conflictInfo);
    }
}
