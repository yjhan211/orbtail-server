using game_server.players.bots;
using network.common;
using network.common.data;

namespace demo_regression_tests;

public sealed class BotPathTests
{
    [Fact]
    public void NewMovementTargetDoesNotReplaceCommittedPath()
    {
        var bot = new Bot
        {
            WasAvoidingMonsterAtLastPathPlan = true,
            Movement = { WaypointIndex = 2 }
        };
        var path = bot.Movement.Waypoints;
        var cell = new network.common.data.models.Cell(3, 4);

        bot.SetMovementTarget(AreaType.S2Gym1, cell);

        Assert.NotNull(bot.Movement.Destination);
        Assert.Equal(AreaType.S2Gym1, bot.Movement.DestinationArea);
        Assert.Equal(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell), bot.Movement.Destination);
        Assert.True(bot.WasAvoidingMonsterAtLastPathPlan);
        Assert.Same(path, bot.Movement.Waypoints);
        Assert.Equal(2, bot.Movement.WaypointIndex);
    }

    [Fact]
    public void ClearingPathPreservesDestinationAndDecisionButCancelsMovement()
    {
        var bot = new Bot { WasAvoidingMonsterAtLastPathPlan = true };
        bot.SetMovementTarget(AreaType.S2Ground,
            new network.common.data.models.Cell(3, 4));
        var destination = bot.Movement.Destination;
        bot.Movement.Waypoints.Add(destination!);
        bot.Movement.WaypointIndex = 1;
        bot.Movement.Speed = 1f;
        bot.Movement.FollowPath = true;

        bot.Movement.Clear();

        Assert.Empty(bot.Movement.Waypoints);
        Assert.Equal(0, bot.Movement.WaypointIndex);
        Assert.Same(destination, bot.Movement.Destination);
        Assert.Equal(AreaType.S2Ground, bot.Movement.DestinationArea);
        Assert.NotNull(bot.Movement.Destination);
        Assert.True(bot.WasAvoidingMonsterAtLastPathPlan);
        Assert.Equal(0f, bot.Movement.Speed);
        Assert.False(bot.Movement.FollowPath);
    }
}
