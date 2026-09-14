using game_server.matches;
using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotCellTargetTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RecentAttackerUsesCellAndEscapeTakesPriorityOverRetaliation(bool stronger, bool wounded)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(981240);
        using var scope = runtime.Enter();
        var area = AreaType.S2Corridor9;
        var origin = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = origin });
        var bot = runtime.Bots.GetBot(-1)!;
        runtime.RegisterParticipant(bot.Player);
        bot.Player.Orbs.TakeAllItems();
        for (int i = 0; i < 4; i++)
        {
            bot.Player.Orbs.AddItem(107000020);
        }
        var targetCell = SwarmPressureField.GetAreaCellsByDistance(area)
            .Select(entry => entry.Cell)
            .First(cell => origin.GetDistance(cell) >= 4 && origin.GetDistance(cell) <= 8 && GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell));
        var attacker = new Player
        {
            Profile = new PlayerInfo { PlayerId = 1 },
            Cell = targetCell,
            // 목표 선택은 이 정밀 위치가 아니라 위 셀을 사용해야 한다.
            Position = new Vector3f(10000f, 10000f, 0f),
            CurrentArea = area,
            Health = Config.MAX_HEALTH
        };
        for (int i = 0; i < (stronger ? 8 : 1); i++)
        {
            attacker.Orbs.AddItem(107000020);
        }
        runtime.RegisterParticipant(attacker);
        bot.Wounded = wounded;
        bot.LastProximityAttackerPlayerId = attacker.PlayerId;
        bot.LastDamagedAtUtc = DateTime.UtcNow;
        var service = new BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance);

        service.SelectMovementTarget(runtime, bot);

        Assert.NotNull(bot.Movement.DestinationCell);
        Assert.Null(bot.Movement.Destination);
        if (stronger || wounded)
        {
            Assert.NotEqual(targetCell, bot.Movement.DestinationCell);
            Assert.True(bot.Movement.DestinationCell!.GetDistance(targetCell) >= origin.GetDistance(targetCell));
        }
        else
        {
            Assert.Equal(targetCell, bot.Movement.DestinationCell);
        }
    }

    [Fact]
    public void TargetCellIsCopiedWithoutChangingWorldPath()
    {
        var bot = new Bot();
        var cell = new Cell(10, 12);
        var waypoint = new Vector3f(1, 2, 0);
        bot.Movement.Waypoints.Add(waypoint);
        bot.SetMovementTarget(AreaType.S2Corridor9, cell);
        cell.X = 999;
        Assert.Equal(new Cell(10, 12), bot.Movement.DestinationCell);
        Assert.Null(bot.Movement.Destination);
        Assert.Same(waypoint, Assert.Single(bot.Movement.Waypoints));
    }

    [Fact]
    public void MonsterPositionInitializesCellBeforeFirstMovementTick()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var monster = new Monster { Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell) };
        Assert.Equal(cell, monster.Info.ObjectInfo.Cell);
    }

    [Fact]
    public void GroundItemCopiesKeepLandingCellWithoutSharingIt()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var items = new MatchGroundItemState();
        var item = Assert.Single(items.SpawnItems(AreaType.S2Corridor9, position.X, position.Y, [Config.SUMMON_STONE_GROUND_ITEM_ID]));
        var expected = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, item.ObjectInfo.Position);
        Assert.Equal(expected, item.ObjectInfo.Cell);
        item.ObjectInfo.Cell.X = -999;
        Assert.Equal(expected, items.GetItem(item.GroundItemUid)!.ObjectInfo.Cell);
    }
}
