using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerOrbOrbitTests
{
    [Theory]
    [InlineData(17)]
    [InlineData(-17)]
    public void FirstEventWithoutOriginOnlyRecordsTheBaseline(long playerId)
    {
        var player = new Player(new PlayerInfo { PlayerId = playerId });
        float initial = SwarmOrbOrbit.InitialPhaseDegrees(playerId);
        Assert.Equal(initial, player.Orbs.OrbitPhaseDegrees);
        player.Orbs.AdvanceOrbit(new Vector3f(10, 20, 0));
        Assert.Equal(initial, player.Orbs.OrbitPhaseDegrees);
        player.Orbs.AdvanceOrbit(new Vector3f(11, 20, 0));
        Assert.Equal(SwarmOrbOrbit.AdvancePhase(initial, 1), player.Orbs.OrbitPhaseDegrees);
    }

    [Fact]
    public void SpawnOriginCountsFirstMovementAndIsCopied()
    {
        var player = new Player(new PlayerInfo { PlayerId = 17 });
        var spawn = new Vector3f(10, 20, 0);
        player.Orbs.ResetOrbit(spawn);
        spawn.X = 100;
        var next = new Vector3f(11, 20, 0);
        player.Orbs.AdvanceOrbit(next);
        float once = SwarmOrbOrbit.AdvancePhase(SwarmOrbOrbit.InitialPhaseDegrees(17), 1);
        Assert.Equal(once, player.Orbs.OrbitPhaseDegrees);
        next.X = 100;
        player.Orbs.AdvanceOrbit(new Vector3f(12, 20, 0));
        Assert.Equal(SwarmOrbOrbit.AdvancePhase(once, 1), player.Orbs.OrbitPhaseDegrees);
    }

    [Fact]
    public void StandingAndTeleportDoNotRotateButTeleportUpdatesBaseline()
    {
        var player = new Player(new PlayerInfo { PlayerId = -17 });
        player.Orbs.ResetOrbit(new Vector3f());
        float initial = player.Orbs.OrbitPhaseDegrees;
        player.Orbs.AdvanceOrbit(new Vector3f());
        Assert.Equal(initial, player.Orbs.OrbitPhaseDegrees);
        float destination = Config.SWARM_ORB_ORBIT_TELEPORT_DISTANCE + 10;
        player.Orbs.AdvanceOrbit(new Vector3f(destination, 0, 0));
        Assert.Equal(initial, player.Orbs.OrbitPhaseDegrees);
        player.Orbs.AdvanceOrbit(new Vector3f(destination + 1, 0, 0));
        Assert.Equal(SwarmOrbOrbit.AdvancePhase(initial, 1), player.Orbs.OrbitPhaseDegrees);
        player.Orbs.ResetOrbit();
        player.Orbs.AdvanceOrbit(new Vector3f(200, 0, 0));
        Assert.Equal(initial, player.Orbs.OrbitPhaseDegrees);
    }
}
