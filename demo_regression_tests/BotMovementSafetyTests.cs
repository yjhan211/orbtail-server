using game_server.matches;
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
            bot.Movement.NextPathPlanAtUtc = DateTime.UtcNow.AddMinutes(1);
            bot.LoopWaitUntil = DateTime.MinValue;
            bot.SetMovementTarget(AreaType.S2Library1, target);
            bot.Movement.Waypoints.Add(target);
            bot.Player.Velocity = new Vector3f(5, 0, 0);
            var originalPosition = bot.Player.Position;
            runtime.Closures.InitializeMatching([(AreaType.S2Library1, 0)]);
            runtime.Closures.CloseDueAreas();
            var result = MovementTickTestDriver.RunBotTick(runtime, _ => { });

            Assert.Empty(bot.Movement.Waypoints);
            Assert.Same(originalPosition, bot.Player.Position);
            Assert.Equal(DateTime.MinValue, bot.Movement.NextPathPlanAtUtc);
            var movement = Assert.Single(result.Movements);
            Assert.Equal(0f, movement.Velocity.X);
            Assert.Equal(0f, movement.Velocity.Y);
            Assert.False(movement.IsAreaTransition);
        }
    }

    [Fact]
    public void OutsideFieldAllowsEqualDistanceStep()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982003);
        using (runtime.Enter())
        {
            // 문으로 접근하거나 통과할 때 인접 셀의 반올림 거리가 같을 수 있다.
            var door = GameDoorData.Get(213)!;
            var cell = new Cell((int)door.PositionX, (int)door.PositionY);
            int distance = SwarmPressureField.GetDistance(cell);
            var neighbors = new[]
            {
                new Cell(cell.X - 1, cell.Y), new Cell(cell.X + 1, cell.Y),
                new Cell(cell.X, cell.Y - 1), new Cell(cell.X, cell.Y + 1)
            };
            var target = neighbors.First(candidate => SwarmPressureField.GetDistance(candidate) == distance);
            var bot = new Bot { PlayerId = -1 };
            bot.Player.CurrentArea = door.AreaType;
            bot.Player.Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
            var now = DateTime.UtcNow;
            runtime.Closures.GameStartTime = now.AddHours(-1);
            Assert.True(distance > runtime.Closures.GetSafeDistance(now));

            Assert.False(MatchMoveService.IsUnsafeStep(runtime, bot.Player.Position!, bot.Player.CurrentArea, target, bot.Player.CurrentArea, now));
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

            Assert.False(MatchMoveService.IsUnsafeStep(runtime, bot.Player.Position!, bot.Player.CurrentArea, inner, bot.Player.CurrentArea, DateTime.UtcNow));
            bot.Player.Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, inner);
            Assert.True(MatchMoveService.IsUnsafeStep(runtime, bot.Player.Position!, bot.Player.CurrentArea, outer, bot.Player.CurrentArea, DateTime.UtcNow));
        }
    }
}
