using game_server.players.bots;
using network.common;
using network.common.data;

namespace demo_regression_tests;

public sealed class BotPathTests
{
    [Fact]
    public void ClearingPathRemovesWaypointsAndResetsIndex()
    {
        var bot = new Bot();
        var destination = new network.common.data.models.Cell(3, 4);
        bot.Movement.Waypoints.Add(destination!);
        bot.Movement.WaypointIndex = 1;

        bot.Movement.Clear();

        Assert.Empty(bot.Movement.Waypoints);
        Assert.Equal(0, bot.Movement.WaypointIndex);
    }
}
