using network.common;
using network.common.data.models;

namespace game_server.players.bots;

/// <summary>확정된 봇 이동과 전환 전·후 구역. 같은 매치 잠금 안에서 즉시 전송한다.</summary>
public class BotMovementEvent
{
    public long BotPlayerId { get; set; }
    public AreaType FromArea { get; set; }
    public AreaType ToArea { get; set; }
    public Cell FromCell { get; set; } = new(0, 0);
    public Cell ToCell { get; set; } = new(0, 0);
    public Vector3f Position { get; set; } = new(0f, 0f, 0f);
    public Vector3f Velocity { get; set; } = new(0f, 0f, 0f);
    public float Rotation { get; set; }
    public bool IsAreaTransition { get; set; }
}
