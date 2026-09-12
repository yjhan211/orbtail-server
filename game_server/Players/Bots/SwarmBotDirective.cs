using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players.bots;

public enum SwarmBotMode
{
    None,
    Defend,
    Escort,
    Return
}

/// <summary>봇 판단 결과. 이번 틱에 어느 구역의 어느 칸으로 갈지를 이동 서비스에 넘긴다.</summary>
public readonly record struct SwarmBotDirective(
    SwarmBotMode Mode,
    AreaType DestinationArea,
    Cell DestinationCell,
    Vector3f DestinationPosition)
{
    public static SwarmBotDirective None => new(
        SwarmBotMode.None, AreaType.None, new Cell(0, 0), new Vector3f(0f, 0f, 0f));
}
