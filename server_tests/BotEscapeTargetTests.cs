using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace server_tests;

public sealed class BotEscapeTargetTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void EscapeUsesSafeDestinationAndAllowsInwardStepsOutsideField(int boundaryOffset)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var mapId = Config.SWARM_MATCH_MAP;
        var area = AreaType.S2Corridor9;
        var currentCell = SwarmPressureField.GetAreaCellsByDistance(area)
            .OrderByDescending(entry => entry.Distance)
            .First(entry => GameMapData.IsMoveablePosition(mapId, entry.Cell)).Cell;
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(981232);
        using var scope = runtime.Enter();
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1L] = currentCell });
        var bot = runtime.Bots.GetBot(-1)!;
        bot.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(area)));
        var now = DateTime.UtcNow;
        double boundary = SwarmPressureField.GetDistance(currentCell) + boundaryOffset + 0.25d;
        double elapsed = SwarmPressureField.HoldSeconds + SwarmPressureField.GetProgressAtSafeDistance(boundary) * SwarmPressureField.ShrinkSeconds;
        runtime.Closures.GameStartTime = now.AddSeconds(-elapsed);
        double safeDistance = runtime.Closures.GetSafeDistance(now);

        var target = BotBehaviorService.SelectThreatEscapeTarget(runtime, bot, currentCell, now);

        Assert.NotNull(target);
        Assert.False(target.Equals(currentCell));
        Assert.True(SwarmPressureField.GetDistance(target) <= safeDistance);
        var path = MapPathfinder.FindPath(mapId, area, currentCell, GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, target!), target);
        Assert.NotNull(path);
        int previousDistance = SwarmPressureField.GetDistance(currentCell);
        foreach (var step in path!)
        {
            int distance = SwarmPressureField.GetDistance(step.Cell);
            Assert.True(distance <= safeDistance || distance <= previousDistance);
            previousDistance = distance;
        }
    }

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
        Assert.NotNull(target);

        var targetArea = GameMapData.GetCurrentArea(mapId, target);
        Assert.NotEqual(AreaType.None, targetArea);
        Assert.True(GameMapData.IsMoveablePosition(mapId, target));
        var path = MapPathfinder.FindPath(mapId, area, cell, targetArea, target);
        Assert.NotNull(path);
        Assert.NotEmpty(path!);
        var threatCell = MapCoordinateConverter.WorldToCell(mapId, threat);
        Assert.True(target.GetDistance(threatCell) > cell.GetDistance(threatCell));
        Assert.InRange(cell.GetDistance(target), Config.SWARM_BOT_MIN_FLEE_TARGET_DISTANCE_CELLS, Config.SWARM_BOT_FLEE_PROBE_DISTANCE_CELLS);
    }

    [Fact]
    public void MonsterEscapeWithoutMatchingAreaCandidateLeavesDestinationUnset()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var target = SelectMonsterEscapeTarget(cell, AreaType.None, cell);
        Assert.Null(target);
    }

    private static Cell? SelectMonsterEscapeTarget(Cell currentCell, AreaType area, Cell threatCell)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(981231);
        using var scope = runtime.Enter();
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1L] = currentCell });
        var bot = runtime.Bots.GetBot(-1)!;
        bot.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(area)));
        return BotBehaviorService.SelectThreatEscapeTarget(runtime, bot, threatCell, DateTime.UtcNow);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(1f)]
    public void EscapeDestinationIsFartherFromThreatAndPathStaysInsideField(float threatOffset)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(981230);
        using (runtime.Enter())
        {
            var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
            runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1L] = cell });
            var bot = runtime.Bots.GetBot(-1)!;
            runtime.RegisterPlayer(bot.Player);
            var position = bot.Player.Position!;
            var threatPosition = new Vector3f(position.X + threatOffset, position.Y, 0f);
            var rival = new Player(new PlayerInfo { PlayerId = 1 })
            {
                Position = threatPosition,
                Cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, threatPosition),

                Health = Config.MAX_HEALTH
            };
            rival.Orbs.TryAddOrbWithCapacity(107000020, 8, out _);
            runtime.RegisterPlayer(rival);
            var service = new BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance);

            bot.MonsterAvoidanceTarget = (cell, DateTime.UtcNow);
            var target = service.SelectMovementTarget(runtime, bot, DateTime.UtcNow);

            Assert.Null(bot.MonsterAvoidanceTarget);
            Assert.NotNull(target);
            Assert.True(target.GetDistance(rival.Cell!) >= cell.GetDistance(rival.Cell!));
            Assert.InRange(cell.GetDistance(target), 0, Config.SWARM_BOT_FLEE_PROBE_DISTANCE_CELLS);
            Assert.NotEqual(AreaType.None, GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, target!));
            Assert.True(GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, target));
            if (target.GetDistance(cell) > 0)
            {
                var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, GameMapData.GetCurrentArea(bot.Player.GameInfo.ObjectInfo.MapId, bot.Player.GameInfo.ObjectInfo.Cell),
                    cell, GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, target!), target);
                Assert.NotNull(path);
                Assert.NotEmpty(path!);
                double safeDistance = runtime.Closures.GetSafeDistance(DateTime.UtcNow);
                Assert.All(path!, step =>
                {
                    Assert.True(SwarmPressureField.GetDistance(step.Cell) <= safeDistance);
                });
            }
        }
    }
}
