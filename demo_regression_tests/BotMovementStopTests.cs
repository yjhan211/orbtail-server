using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotMovementStopTests
{
    [Fact]
    public void PreparesAllBotsBeforeApplyingAnyMovement()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982011);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1L, -2L],
            new Dictionary<long, Cell> { [-1] = cell, [-2] = cell });
        foreach (var bot in runtime.Bots.GetBots())
        {
            bot.Player.Velocity = new Vector3f(5, 0, 0);
            bot.Movement.Destination = null;
        }
        int decisions = 0;
        MovementTickTestDriver.RunBotTick(runtime, _ =>
        {
            decisions++;
            // 이전 개체의 정지조차 아직 적용되지 않아야 한다.
            foreach (var bot in runtime.Bots.GetBots())
                Assert.Equal(5f, bot.Player.Velocity.X);
        });
        Assert.Equal(2, decisions);
        foreach (var bot in runtime.Bots.GetBots())
            Assert.Equal(0f, bot.Player.Velocity.X);
    }
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
            bot.Movement.NextPathPlanAtUtc = DateTime.UtcNow.AddMinutes(1);
            bot.LoopWaitUntil = DateTime.MinValue;
            var originalPosition = bot.Player.Position!;
            bot.Player.Velocity = new Vector3f(5f, 1f, 0f);
            if (reason != "no_target")
                bot.SetMovementTarget(bot.Player.CurrentArea, cell);
            if (reason is "waiting" or "blocked_cell")
                bot.Movement.Waypoints.Add(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
                    reason == "blocked_cell" ? new Cell(-10000, -10000) : cell));
            if (reason == "waiting")
                bot.LoopWaitUntil = DateTime.UtcNow.AddMinutes(1);
            var first = MovementTickTestDriver.RunBotTick(runtime, _ => { });

            var stopped = Assert.Single(first.Movements);
            Assert.Equal(0f, stopped.Velocity.X);
            Assert.Equal(0f, stopped.Velocity.Y);
            Assert.Same(originalPosition, stopped.Position);
            Assert.False(stopped.IsAreaTransition);
            var second = MovementTickTestDriver.RunBotTick(runtime, _ => { });
            Assert.Empty(second.Movements);
        }
    }
}
