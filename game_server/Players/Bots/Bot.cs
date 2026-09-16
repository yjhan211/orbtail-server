using game_server.matches;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players.bots;

public class Bot
{
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
    public DateTime LastDamagedAtUtc { get; set; } = DateTime.MinValue;
    public (Cell Destination, DateTime SelectedAtUtc)? MonsterAvoidanceTarget { get; set; }
    public DateTime? LastTrailCutAtUtc { get; set; }
    public bool Wounded { get; set; }
    public DateTime BootsSpeedUntilUtc { get; set; } = DateTime.MinValue;
    public DateTime SwarmBareSpeedUntilUtc { get; set; } = DateTime.MinValue;
}
