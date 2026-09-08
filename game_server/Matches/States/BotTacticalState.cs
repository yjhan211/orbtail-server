using network.common;
using network.common.data;
using network.common.data.models;
using game_server.services;

namespace game_server.matches.states;

/// <summary>봇 전술 상태 — 도주·부상·구역 기억·캠프 순례·회복 페이싱.</summary>
public sealed class BotTacticalState
{
    public readonly Dictionary<(long MatchingId, long PlayerId),
        (AreaType Area, AreaType PreviousArea, DateTime LeftAtUtc)> AreaMemory = new();

    // 이번 틱 지시가 도주·대피였는가 — 왕복 억제의 유일한 예외.
    public readonly HashSet<(long MatchingId, long PlayerId)> FleeDirective = new();

    // 절단 후 회수 창 (#226 F): 이 시간 동안은 약자 추격보다 바닥 소환석 회수가 먼저다.
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> LastTrailCutAtUtc = new();
    public readonly HashSet<(long MatchingId, long PlayerId)> Wounded = new();

    // 추격 로그 스로틀 — 같은 쌍은 3초에 한 번만 남긴다. 판단은 50ms마다 돈다.
    public readonly Dictionary<(long MatchingId, long ChaserId, long TargetId), DateTime> ChaseLogThrottle = new();

    // 봇 체력 자연 회복: 마지막 피격 후 유예가 지나면 초당 일정량 회복한다.
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> LastDamagedAtUtc = new();
    public readonly Dictionary<(long MatchingId, long PlayerId), DateTime> NextRecoveryAtUtc = new();
}
