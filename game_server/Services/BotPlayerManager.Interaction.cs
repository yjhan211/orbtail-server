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
}
