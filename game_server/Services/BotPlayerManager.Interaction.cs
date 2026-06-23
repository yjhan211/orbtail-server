using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     봇 1:1 동기턴 자동 응답 AI.
///     #26 Part 3:
///     - 선택지 자동 선택 (질문 4 카테고리 무작위)
///     - 답변 자동 선택 (진실 1 / 거짓 2 비율)
///     - 직책 밝히기 자동 (가끔 블러프)
/// </summary>
public partial class BotPlayerManager
{
    /// <summary>
    ///     직책별 총 부품 수 (소재 4 + 중간재 2 + 최종 1 = 7).
    /// </summary>
    private static int GameProgressTotal(short jobTitle)
    {
        return GameMissionData.GetTotalParts(jobTitle);
    }

    /// <summary>
    ///     봇이 질문자일 때 무작위 질문 카테고리 선택.
    ///     4 카테고리: JOB(직책추궁) / MOVEMENT(동선추궁) / CROSS_VERIFY(교차검증) / TRACE(흔적추궁).
    ///     테스트 데이터 다양성을 위해 균등 분포.
    /// </summary>
    public InteractionQuestionType PickRandomQuestion()
    {
        // ASK_JOB(1) / ASK_LOCATION(2) / CROSS_CHECK(3) / ASK_TRACE(4)
        InteractionQuestionType[] values =
        {
            InteractionQuestionType.ASK_JOB,
            InteractionQuestionType.ASK_LOCATION,
            InteractionQuestionType.CROSS_CHECK,
            InteractionQuestionType.ASK_TRACE
        };
        return values[_rng.Next(values.Length)];
    }

    /// <summary>
    ///     봇이 답변자일 때 답변 인덱스 선택. 진실 1 / 거짓 2 비율 (블러프 시뮬).
    ///     답변 후보 수가 적어도 0..n-1에서 가중 랜덤으로 선택.
    /// </summary>
    public int PickAnswerIndex(int answerCount)
    {
        if (answerCount <= 1) return 0;

        // 진실(0번)이 33% 정도 — 거짓 2개에 더 분산
        int roll = _rng.Next(100);
        if (roll < 34) return 0;
        return 1 + _rng.Next(answerCount - 1);
    }

    /// <summary>
    ///     봇이 직책을 자발적으로 밝힐지 결정. 자기 race 진행이 50% 미만이면 50% 진실, 50% 블러프.
    ///     50% 이상이면 침묵 우선(밝히지 않음).
    /// </summary>
    public BotJobReveal DecideRevealJob(long matchingId, long botPlayerId, MissionManager missionManager)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return BotJobReveal.Silent;

        var state = missionManager.GetState(matchingId, botPlayerId);
        int collected = state?.CollectedParts.Count ?? 0;
        int total = GameProgressTotal((short)bot.MyJobTitle);

        if (total > 0 && collected * 100 / total >= 50) return BotJobReveal.Silent;

        // 50% 진실 / 50% 블러프
        return _rng.Next(100) < 50 ? BotJobReveal.Truth : BotJobReveal.Bluff;
    }

    /// <summary>
    ///     봇이 마지막으로 응답한 상대 기록 (동일 상대 연속 응답 방지)
    /// </summary>
    public void NoteRespondedTo(long matchingId, long botPlayerId, long requesterPlayerId)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return;
        bot.LastInteractRespondedTo = requesterPlayerId;
    }
}

/// <summary>
///     봇 직책 밝히기 모드.
/// </summary>
public enum BotJobReveal
{
    Silent,  // 밝히지 않음
    Truth,   // 진실 (실제 직책)
    Bluff    // 블러프 (다른 직책 사칭)
}
