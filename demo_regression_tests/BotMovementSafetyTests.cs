using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotMovementSafetyTests
{
    [Fact]
    public void ClosedDestinationPreservesPathWhileStoppedAndPublishesStop()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982001);
        using (runtime.Enter())
        {
            var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
            var target = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Library1);
            runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
            var bot = runtime.Bots.GetBot(-1)!;
            var deadline = DateTime.UtcNow.AddMinutes(1);
            bot.Movement.NextPathPlanAtUtc = deadline;
            bot.LoopWaitUntil = DateTime.MinValue;
            bot.SetMovementTarget(AreaType.S2Library1, target);
            bot.Movement.Waypoints.Add(target);
            bot.Player.Velocity = new Vector3f(5, 0, 0);
            var originalPosition = bot.Player.Position;
            runtime.Closures.InitializeMatching([(AreaType.S2Library1, 0)]);
            runtime.Closures.CloseDueAreas();
            var result = MovementTickTestDriver.RunBotTick(runtime, _ => { });

            Assert.Same(target, Assert.Single(bot.Movement.Waypoints));
            Assert.Same(originalPosition, bot.Player.Position);
            Assert.Equal(deadline, bot.Movement.NextPathPlanAtUtc);
            var movement = Assert.Single(result.Movements);
            Assert.Equal(0f, movement.Velocity.X);
            Assert.Equal(0f, movement.Velocity.Y);
            Assert.False(movement.IsAreaTransition);
        }
    }
}
