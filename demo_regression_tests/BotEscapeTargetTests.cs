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

        var target = BotBehaviorService.FindMonsterEscapeCell(position, area, threat);

        Assert.Equal(area, GameMapData.GetCurrentArea(mapId, target));
        Assert.True(GameMapData.IsMoveablePosition(mapId, target));
        var path = MapPathfinder.FindPath(mapId, area, cell, area, target);
        Assert.NotNull(path);
        Assert.NotEmpty(path!);
        Assert.All(path!, step => Assert.Equal(area, GameMapData.GetCurrentArea(mapId, step.Cell)));
        var destination = MapCoordinateConverter.CellToWorld(mapId, target);
        float dx = destination.X - threat.X;
        float dy = destination.Y - threat.Y;
        Assert.True(dx * dx + dy * dy > offsetX * offsetX + offsetY * offsetY);
        float moveX = destination.X - position.X;
        float moveY = destination.Y - position.Y;
        Assert.True(moveX * moveX + moveY * moveY <= Config.SWARM_BOT_MONSTER_FLEE_DISTANCE * Config.SWARM_BOT_MONSTER_FLEE_DISTANCE);
    }

    [Fact]
    public void MonsterEscapeWithoutMatchingAreaCandidateKeepsCurrentCell()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var target = BotBehaviorService.FindMonsterEscapeCell(position, AreaType.None, position);
        Assert.Equal(cell.X, target.X);
        Assert.Equal(cell.Y, target.Y);
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
                CurrentArea = bot.Player.CurrentArea,
                Health = Config.MAX_HEALTH
            };
            rival.Orbs.TryAddItemWithCapacity(107000020, 8, out _);
            runtime.RegisterParticipant(rival);
            var service = new BotBehaviorService(null!, null!, null!, NullLogger<BotBehaviorService>.Instance);

            bot.MonsterAvoidanceTarget = (position, DateTime.UtcNow);
            service.SelectMovementTarget(runtime, bot);

            Assert.Null(bot.MonsterAvoidanceTarget);
            var target = bot.Movement.Destination!;
            float dx = target.X - threatPosition.X;
            float dy = target.Y - threatPosition.Y;
            Assert.True(dx * dx + dy * dy >= threatOffset * threatOffset);
            Assert.NotEqual(AreaType.None, bot.Movement.DestinationArea);
            Assert.True(GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, bot.Movement.Destination!)));
        }
    }
}
