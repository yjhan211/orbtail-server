using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotMovementSafetyTests
{
    [Fact]
    public void ClosedDestinationClearsExistingPathAndPublishesStop()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982001);
        using (runtime.Enter())
        {
            var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
            var target = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Library1);
            runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
            var bot = runtime.Bots.GetBot(-1)!;
            bot.MovementMode = BotMovementMode.Escort;
            bot.MovementModeUntilUtc = DateTime.UtcNow.AddMinutes(1);
            bot.LoopWaitUntil = DateTime.MinValue;
            bot.SetMovementTarget(BotMovementMode.Escort, AreaType.S2Library1, target,
                MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, target));
            bot.SetPath([new MapPathfinder.Step { Cell = target, Area = AreaType.S2Library1 }]);
            bot.Player.Velocity = new Vector3f(5, 0, 0);
            var originalPosition = bot.Player.Position;
            runtime.Closures.InitializeMatching([(AreaType.S2Library1, 0)]);
            runtime.Closures.CloseDueAreas();

            var service = new BotMovementService(NullLogger<BotMovementService>.Instance);
            var result = service.ProcessBotMovementTick(runtime, new Dictionary<long, AreaType>(), _ => { });

            Assert.Empty(bot.Path);
            Assert.Same(originalPosition, bot.Player.Position);
            Assert.Equal(DateTime.MinValue, bot.MovementModeUntilUtc);
            var movement = Assert.Single(result.Movements);
            Assert.Equal(0f, movement.Velocity.X);
            Assert.Equal(0f, movement.Velocity.Y);
            Assert.False(movement.IsAreaTransition);
        }
    }

    [Fact]
    public void OutsideFieldAllowsInwardEscapeButRejectsOutwardStep()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982002);
        using (runtime.Enter())
        {
            var cells = GameMapData.GetAreas(Config.SWARM_MATCH_MAP)
                .Select(area => GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area.AreaType))
                .OrderBy(SwarmPressureField.GetDistance).ToList();
            var inner = cells.First();
            var outer = cells.Last();
            Assert.True(SwarmPressureField.GetDistance(outer) > SwarmPressureField.GetDistance(inner));
            var bot = new Bot { PlayerId = -1 };
            bot.Player.CurrentArea = AreaType.S2Corridor9;
            bot.Player.Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, outer);
            runtime.Closures.GameStartTime = DateTime.UtcNow.AddHours(-1);

            Assert.False(BotMovementService.IsUnsafeStep(runtime, bot, inner, bot.Player.CurrentArea, DateTime.UtcNow));
            bot.Player.Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, inner);
            Assert.True(BotMovementService.IsUnsafeStep(runtime, bot, outer, bot.Player.CurrentArea, DateTime.UtcNow));
        }
    }
}
