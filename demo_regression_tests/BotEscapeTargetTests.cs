using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotEscapeTargetTests
{
    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(1f, 0f)]
    [InlineData(0f, -1f)]
    [InlineData(0f, 1f)]
    public void MonsterEscapeCellIsReachableAndFartherFromThreat(float offsetX, float offsetY)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var mapId = Config.SWARM_MATCH_MAP;
        var area = AreaType.S2Corridor9;
        var cell = GameMapData.GetAreaSpawnCell(mapId, area);
        var position = MapCoordinateConverter.CellToWorld(mapId, cell);
        var threat = new Vector3f(position.X + offsetX, position.Y + offsetY, 0f);

        var target = SelectMonsterEscapeTarget(cell, area, MapCoordinateConverter.WorldToCell(mapId, threat));

        Assert.Equal(area, GameMapData.GetCurrentArea(mapId, target));
        Assert.True(GameMapData.IsMoveablePosition(mapId, target));
        var path = MapPathfinder.FindPath(mapId, area, cell, area, target);
        Assert.NotNull(path);
        Assert.NotEmpty(path!);
        Assert.All(path!, step => Assert.Equal(area, GameMapData.GetCurrentArea(mapId, step.Cell)));
        var threatCell = MapCoordinateConverter.WorldToCell(mapId, threat);
        Assert.True(target.GetDistance(threatCell) > cell.GetDistance(threatCell));
        Assert.InRange(cell.GetDistance(target), Config.SWARM_BOT_MIN_THREAT_FLEE_DISTANCE_CELLS, Config.SWARM_BOT_MONSTER_FLEE_DISTANCE_CELLS);
    }

    [Fact]
    public void MonsterEscapeWithoutMatchingAreaCandidateKeepsCurrentCell()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var target = SelectMonsterEscapeTarget(cell, AreaType.None, cell);
        Assert.Equal(cell.X, target.X);
        Assert.Equal(cell.Y, target.Y);
    }

    private static Cell SelectMonsterEscapeTarget(Cell currentCell, AreaType area, Cell threatCell)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(981231);
        using var scope = runtime.Enter();
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1L] = currentCell });
        var bot = runtime.Bots.GetBot(-1)!;
        bot.Player.CurrentArea = area;
        BotBehaviorService.SelectThreatEscapeTarget(runtime, bot, threatCell, DateTime.UtcNow, stayInCurrentArea: true);
        var destination = bot.Movement.DestinationCell!;
        Assert.Equal(destination.GetDistance(currentCell) == 0, bot.Movement.DestinationCell!.Equals(bot.Player.Cell));
        return destination;
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(1f)]
    public void EscapeTargetNeverMovesCloserToThreat(float threatOffset)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(981230);
        using (runtime.Enter())
        {
            var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
            runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1L] = cell });
            var bot = runtime.Bots.GetBot(-1)!;
            runtime.RegisterParticipant(bot.Player);
            var position = bot.Player.Position!;
            var threatPosition = new Vector3f(position.X + threatOffset, position.Y, 0f);
            var rival = new Player
            {
                Profile = new PlayerInfo { PlayerId = 1 },
                Position = threatPosition,
                Cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, threatPosition),
                CurrentArea = bot.Player.CurrentArea,
                Health = Config.MAX_HEALTH
            };
            rival.Orbs.TryAddItemWithCapacity(107000020, 8, out _);
            runtime.RegisterParticipant(rival);
            var service = new BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance);

            bot.MonsterAvoidanceTarget = (cell, DateTime.UtcNow);
            service.SelectMovementTarget(runtime, bot);

            Assert.Null(bot.MonsterAvoidanceTarget);
            var target = bot.Movement.DestinationCell!;
            Assert.True(target.GetDistance(rival.Cell!) >= cell.GetDistance(rival.Cell!));
            Assert.InRange(cell.GetDistance(target), 0, Config.SWARM_BOT_FLEE_PROBE_DISTANCE_CELLS);
            Assert.NotEqual(AreaType.None, bot.Movement.DestinationArea);
            Assert.True(GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, target));
            if (target.GetDistance(cell) > 0)
            {
                var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, bot.Player.CurrentArea,
                    cell, bot.Movement.DestinationArea, target);
                Assert.NotNull(path);
                Assert.NotEmpty(path!);
                double safeDistance = runtime.Closures.GetSafeDistance(DateTime.UtcNow);
                Assert.All(path!, step =>
                {
                    Assert.True(step.Cell.GetDistance(rival.Cell!) >= cell.GetDistance(rival.Cell!));
                    Assert.True(SwarmPressureField.GetDistance(step.Cell) <= safeDistance);
                });
            }
        }
    }
}
