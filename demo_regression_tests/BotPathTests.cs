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
            MovementMode = BotMovementMode.Return,
            MovementDestination = AreaType.S2Ground,
            Movement = { WaypointIndex = 2 }
        };
        var path = bot.Movement.Waypoints;
        var cell = new network.common.data.models.Cell(3, 4);

        bot.SetMovementTarget(BotMovementMode.Escort, AreaType.S2Gym1, cell);

        Assert.Equal(BotMovementMode.Escort, bot.DesiredMovementMode);
        Assert.Equal(AreaType.S2Gym1, bot.DesiredMovementArea);
        Assert.Same(cell, bot.DesiredMovementCell);
        Assert.Equal(BotMovementMode.Return, bot.MovementMode);
        Assert.Equal(AreaType.S2Ground, bot.MovementDestination);
        Assert.Same(path, bot.Movement.Waypoints);
        Assert.Equal(2, bot.Movement.WaypointIndex);
    }

    [Fact]
    public void ReplacingAndClearingPathResetsProgressWithoutChangingDecision()
    {
        var bot = new Bot
        {
            Movement = { WaypointIndex = 3 },
            MovementDestination = AreaType.S2Ground,
            MovementMode = BotMovementMode.Return
        };
        var path = new List<MapPathfinder.Step>();

        bot.SetPath(path);

        Assert.NotSame(path, bot.Movement.Waypoints);
        Assert.Empty(bot.Movement.Waypoints);
        Assert.Equal(0, bot.Movement.WaypointIndex);
        Assert.Equal(AreaType.S2Ground, bot.MovementDestination);
        Assert.Equal(BotMovementMode.Return, bot.MovementMode);

        bot.Movement.WaypointIndex = 2;
        bot.ClearPath();

        Assert.Empty(bot.Movement.Waypoints);
        Assert.Equal(0, bot.Movement.WaypointIndex);
        Assert.Equal(AreaType.S2Ground, bot.MovementDestination);
        Assert.Equal(BotMovementMode.Return, bot.MovementMode);
    }
}
