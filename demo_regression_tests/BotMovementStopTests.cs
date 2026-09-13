using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotMovementStopTests
{
    [Theory]
    [InlineData("no_target")]
    [InlineData("no_path")]
    [InlineData("waiting")]
    [InlineData("blocked_cell")]
    public void InterruptedMovementStopsOnce(string reason)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982010);
        using (runtime.Enter())
        {
            var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
            runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
            var bot = runtime.Bots.GetBot(-1)!;
            bot.MovementMode = BotMovementMode.Escort;
            bot.MovementModeUntilUtc = DateTime.UtcNow.AddMinutes(1);
            bot.LoopWaitUntil = DateTime.MinValue;
            var originalPosition = bot.Player.Position!;
            bot.Player.Velocity = new Vector3f(5f, 1f, 0f);
            if (reason != "no_target")
                bot.SetMovementTarget(BotMovementMode.Escort, bot.Player.CurrentArea, cell, originalPosition);
            if (reason is "waiting" or "blocked_cell")
                bot.SetPath([new MapPathfinder.Step
                {
                    Area = bot.Player.CurrentArea,
                    Cell = reason == "blocked_cell" ? new Cell(-10000, -10000) : cell
                }]);
            if (reason == "waiting")
                bot.LoopWaitUntil = DateTime.UtcNow.AddMinutes(1);

            var service = new BotBehaviorService(null!, null!, null!, NullLogger<BotBehaviorService>.Instance, new game_server.matches.MatchMovementService());
            var first = service.ProcessBotMovementTick(runtime, new Dictionary<long, AreaType>(), _ => { });

            var stopped = Assert.Single(first.Movements);
            Assert.Equal(0f, stopped.Velocity.X);
            Assert.Equal(0f, stopped.Velocity.Y);
            Assert.Same(originalPosition, stopped.Position);
            Assert.False(stopped.IsAreaTransition);
            var second = service.ProcessBotMovementTick(runtime, new Dictionary<long, AreaType>(), _ => { });
            Assert.Empty(second.Movements);
        }
    }
}
