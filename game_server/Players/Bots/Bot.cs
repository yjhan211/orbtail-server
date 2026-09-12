using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players.bots;

public class Bot
{
    public Player Player { get; } = new()
    {
        Profile = new PlayerInfo(),
        Cell = new(0, 0),
        Position = new(0f, 0f, 0f)
    };
    public long PlayerId { get => Player.PlayerId; set => Player.Profile.PlayerId = value; }
    public long LastProximityAttackerPlayerId { get; set; }
    public List<MapPathfinder.Step> Path { get; set; } = new();
    public int PathIndex { get; set; }
    public DateTime LastWalkStepTime { get; set; } = DateTime.UtcNow;
    public DateTime LoopWaitUntil { get; set; } = DateTime.MinValue;
    public AreaType LastLockedDoorBlockArea { get; set; } = AreaType.None;
    public float SwarmDodgeDirectionX { get; set; }
    public float SwarmDodgeDirectionY { get; set; }
    public DateTime SwarmDodgeHoldUntilUtc { get; set; } = DateTime.MinValue;
    public AreaType EvacuationDestination { get; set; } = AreaType.None;
    public AreaType MovementDestination { get; set; } = AreaType.None;
    public BotMovementMode MovementMode { get; set; } = BotMovementMode.None;
    public DateTime MovementModeUntilUtc { get; set; } = DateTime.MinValue;
    public DateTime LastDamagedAtUtc { get; set; } = DateTime.MinValue;
    public (AreaType Area, AreaType PreviousArea, DateTime LeftAtUtc)? AreaMemory { get; set; }
    public bool FleeDirective { get; set; }
    public (Vector3f Destination, DateTime CommittedAtUtc)? FleeCommitment { get; set; }
    public DateTime? LastTrailCutAtUtc { get; set; }
    public bool Wounded { get; set; }
    public Vector3f? IdleWatchLastPosition { get; set; }
    public DateTime IdleWatchLastMovedAtUtc { get; set; } = DateTime.MinValue;
    public DateTime IdleWatchLastLoggedAtUtc { get; set; } = DateTime.MinValue;
    public DateTime NextIdleWanderAtUtc { get; set; } = DateTime.MinValue;
    public DateTime BootsSpeedUntilUtc { get; set; } = DateTime.MinValue;
    public bool IsSwarmBareHanded { get; set; }
    public DateTime SwarmBareSpeedUntilUtc { get; set; } = DateTime.MinValue;
}
