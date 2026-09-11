using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players.bots;

/// <summary>봇 전술 상태 — 도주·부상·구역 기억·캠프 순례·회복 페이싱.</summary>
public sealed class BotTacticalState
{
    public readonly Dictionary<long,
        (AreaType Area, AreaType PreviousArea, DateTime LeftAtUtc)> AreaMemory = new();

    // 이번 틱 지시가 도주·대피였는가 — 왕복 억제의 유일한 예외.
    public readonly HashSet<long> FleeDirective = new();

    // 절단 후 회수 창 (#226 F): 이 시간 동안은 약자 추격보다 바닥 소환석 회수가 먼저다.
    public readonly Dictionary<long, DateTime> LastTrailCutAtUtc = new();
    public readonly HashSet<long> Wounded = new();

    // 추격 로그 스로틀 — 같은 쌍은 3초에 한 번만 남긴다. 판단은 50ms마다 돈다.
    public readonly Dictionary<(long ChaserId, long TargetId), DateTime> ChaseLogThrottle = new();

    // 최근 피격을 바탕으로 도주·추격을 판단한다.
    public readonly Dictionary<long, DateTime> LastDamagedAtUtc = new();
}
