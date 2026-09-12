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
            PathIndex = 2
        };
        var path = bot.Path;
        var cell = new network.common.data.models.Cell(3, 4);
        var position = new network.common.data.models.Vector3f(10, 20, 0);

        bot.SetMovementTarget(BotMovementMode.Escort, AreaType.S2Gym1, cell, position);

        Assert.Equal(BotMovementMode.Escort, bot.DesiredMovementMode);
        Assert.Equal(AreaType.S2Gym1, bot.DesiredMovementArea);
        Assert.Same(cell, bot.DesiredMovementCell);
        Assert.Same(position, bot.DesiredMovementPosition);
        Assert.Equal(BotMovementMode.Return, bot.MovementMode);
        Assert.Equal(AreaType.S2Ground, bot.MovementDestination);
        Assert.Same(path, bot.Path);
        Assert.Equal(2, bot.PathIndex);
    }

    [Fact]
    public void ReplacingAndClearingPathResetsProgressWithoutChangingDecision()
    {
        var bot = new Bot
        {
            PathIndex = 3,
            MovementDestination = AreaType.S2Ground,
            MovementMode = BotMovementMode.Return
        };
        var path = new List<MapPathfinder.Step>();

        bot.SetPath(path);

        Assert.Same(path, bot.Path);
        Assert.Equal(0, bot.PathIndex);
        Assert.Equal(AreaType.S2Ground, bot.MovementDestination);
        Assert.Equal(BotMovementMode.Return, bot.MovementMode);

        bot.PathIndex = 2;
        bot.ClearPath();

        Assert.Empty(bot.Path);
        Assert.Equal(0, bot.PathIndex);
        Assert.Equal(AreaType.S2Ground, bot.MovementDestination);
        Assert.Equal(BotMovementMode.Return, bot.MovementMode);
    }
}
