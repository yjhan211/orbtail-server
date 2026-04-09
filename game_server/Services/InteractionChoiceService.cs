using network.common;
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

        // 1. 직책 추궁 (항상 포함)
        questions.Add(new InteractionQuestion
        {
            QuestionType = InteractionQuestionType.ASK_JOB,
            Text = "너 무슨 직책이야?"
        });

        // 2. 동선 추궁 (상대의 이전 구역 정보가 있을 때)
        if (answererPreviousArea.HasValue && answererPreviousArea.Value != AreaType.None)
        {
            questions.Add(new InteractionQuestion
            {
                QuestionType = InteractionQuestionType.ASK_LOCATION,
                Text = $"{answererPreviousArea.Value} 구역에서 방금 나왔지?",
                ReferenceArea = answererPreviousArea.Value
            });
        }

        // 3. 교차 검증 (이전 조우에서 상대가 주장한 직책과 충돌 가능성)
        var askerLogs = _logManager.GetLogs(matchingId, askerPlayerId);
        var answererPreviousClaim = askerLogs
            .Where(l => l.OtherPlayerId == answererPlayerId)
            .OrderByDescending(l => l.Timestamp)
            .FirstOrDefault();

        if (answererPreviousClaim != null)
        {
            // 상대가 이전에 주장한 직책이 있으면, 다른 플레이어도 같은 직책을 주장했는지 확인
            var sameClaim = askerLogs
                .Where(l => l.OtherPlayerId != answererPlayerId &&
                            l.ClaimedJobTitle == answererPreviousClaim.ClaimedJobTitle)
                .FirstOrDefault();

            if (sameClaim != null)
            {
                questions.Add(new InteractionQuestion
                {
                    QuestionType = InteractionQuestionType.CROSS_CHECK,
                    Text = $"다른 사람도 {GetJobTitleKorean(answererPreviousClaim.ClaimedJobTitle)}(이)라고 하던데?",
                    ReferencePlayerId = sameClaim.OtherPlayerId
                });
            }
            else
            {
                // 이전에 다른 직책을 주장했는지 확인 (거짓말 추궁)
                questions.Add(new InteractionQuestion
                {
                    QuestionType = InteractionQuestionType.CROSS_CHECK,
                    Text = $"저번에 {GetJobTitleKorean(answererPreviousClaim.ClaimedJobTitle)}(이)라고 했잖아. 정말이야?"
                });
            }
        }

        // 4. 흔적 추궁 (해당 구역에 흔적이 있을 때) - 질문 생성만 하고 실제 흔적 존재 여부는 외부에서 판단
        questions.Add(new InteractionQuestion
        {
            QuestionType = InteractionQuestionType.ASK_TRACE,
            Text = "여기 누가 온 흔적이 있던데, 혹시 알아?"
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

        // 진실 답변
        answers.Add(new InteractionAnswer
        {
            IsTrue = true,
            ClaimedJob = realJob,
            Text = $"나는 {GetJobTitleKorean(realJob)}이야."
        });

        // 거짓 답변: 실제 직책이 아닌 다른 직책 중 랜덤 1~2개
        var fakeJobs = Enum.GetValues<JobTitle>()
            .Where(j => j != JobTitle.NONE && j != realJob)
            .OrderBy(_ => Random.Shared.Next())
            .Take(2)
            .ToList();

        foreach (var fakeJob in fakeJobs)
        {
            answers.Add(new InteractionAnswer
            {
                IsTrue = false,
                ClaimedJob = fakeJob,
                Text = $"나는 {GetJobTitleKorean(fakeJob)}이야."
            });
        }

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
            conflictInfo = $"{GetJobTitleKorean(claimedJob)}을(를) 주장하는 사람이 여러 명 발견되었습니다!";
        }

        return (isFakeDetected, conflictInfo);
    }

    private static string GetJobTitleKorean(JobTitle job) => job switch
    {
        JobTitle.HEALTH_COMMITTEE => "보건위원",
        JobTitle.BROADCAST_MEMBER => "방송부원",
        JobTitle.DISCIPLINE_MEMBER => "선도부원",
        JobTitle.LIBRARY_COMMITTEE => "도서위원",
        JobTitle.SPORTS_CAPTAIN => "체육부장",
        _ => "알 수 없음"
    };
}
