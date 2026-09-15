using game_server.matches;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players.bots;

public class Bot
{
    /// <summary>목적지만 갱신한다. 확정 경로는 재계획 차례에 교체한다.</summary>
    public void SetMovementTarget(AreaType area, Cell cell)
    {
        Movement.DestinationArea = area;
        Movement.DestinationCell = cell.Clone();
    }

    internal void UpdateWoundedState()
    {
        bool wounded = Wounded;
        float ratio = Player.Health / (float)Config.MAX_HEALTH;
        if (!wounded && ratio <= Config.SWARM_BOT_WOUNDED_ENTER_RATIO)
        {
            Wounded = true;
            return;
        }

        if (wounded && ratio >= Config.SWARM_BOT_WOUNDED_EXIT_RATIO)
        {
            Wounded = false;
            return;
        }

    }

    public Player Player { get; } = new()
    {
        Profile = new PlayerInfo(),
        Cell = new(0, 0),
        Position = new(0f, 0f, 0f)
    };
    public long PlayerId { get => Player.PlayerId; set => Player.Profile.PlayerId = value; }
    public long LastProximityAttackerPlayerId { get; set; }
    public MovementState Movement { get; } = new();
    public (AreaType Area, Cell Cell)? ExplorationTarget { get; set; }
    public DateTime LoopWaitUntil { get; set; } = DateTime.MinValue;
    public Cell? DodgeTargetCell { get; set; }
    public DateTime SwarmDodgeHoldUntilUtc { get; set; } = DateTime.MinValue;
    public DateTime LastDamagedAtUtc { get; set; } = DateTime.MinValue;
    public (Cell Destination, DateTime SelectedAtUtc)? MonsterAvoidanceTarget { get; set; }
    public DateTime? LastTrailCutAtUtc { get; set; }
    public bool Wounded { get; set; }
    public DateTime BootsSpeedUntilUtc { get; set; } = DateTime.MinValue;
    public DateTime SwarmBareSpeedUntilUtc { get; set; } = DateTime.MinValue;
}
