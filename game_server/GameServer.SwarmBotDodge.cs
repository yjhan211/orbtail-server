using game_server.services;
using network.common;
using network.common.data.models;

namespace game_server;

/// <summary>
///     봇 투사체 회피 — 판정(순수 기하)은 Services/SwarmBotDodgePolicy가 담당한다 (#297).
///     각 매치 runtime이 자기 Crossfire 모양에서 불변 스냅샷을 발행하고 봇은 그 배열만 읽는다.
/// </summary>
internal partial class GameServer
{
    /// <summary>이 봇이 지금 비켜서야 할 방향 — 최신 스냅샷 기준 (BotPlayerManager에 배선).</summary>
    internal SwarmBotDodgeAdvice? ResolveSwarmBotDodgeDirection(
        long matchingId, long botPlayerId, Vector3f position, AreaType area, DateTime nowUtc)
    {
        if (matchRuntimes.Get(matchingId) is not { IsTerminal: false } runtime)
            return null;

        return SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            runtime.Swarm.Crossfire.DodgeSnapshot, matchingId, botPlayerId, position, area, nowUtc);
    }
}
