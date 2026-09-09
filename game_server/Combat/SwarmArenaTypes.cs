using game_server.bots;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.combat;

/// <summary>
///     스웜 엔진(SwarmArena/BotPlayerManager)이 공유하는 참가자 공간·피해·봇 지시 값 타입.
///     스팟 아레나 세대의 SpotArena* 이름을 #325에서 스웜 어휘로 개명했다.
/// </summary>

public readonly record struct SwarmParticipantSpatial(
    long PlayerId,
    AreaType Area,
    Vector3f Position);

public readonly record struct SwarmPlayerDamage(
    int MonsterId,
    long TargetPlayerId,
    AreaType Area,
    int Damage);

public enum SwarmBotMode
{
    None,
    Defend,
    Escort,
    Return
}

public readonly record struct SwarmBotDirective(
    SwarmBotMode Mode,
    AreaType DestinationArea,
    Cell DestinationCell,
    Vector3f DestinationPosition)
{
    public static SwarmBotDirective None => new(
        SwarmBotMode.None, AreaType.None, new Cell(0, 0), new Vector3f(0f, 0f, 0f));
}
