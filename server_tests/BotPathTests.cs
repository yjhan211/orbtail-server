using game_server.players.bots;
namespace server_tests;

public sealed class BotPathTests
{
    public BotPathTests() => UserServerMatchingTestData.EnsureGameDataLoaded();

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
