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
    [Fact]
    public void ArmedBotWandersInsteadOfChasingMonsterInAnotherArea()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(981241);
        using var scope = runtime.Enter();
        var area = AreaType.S2Corridor9;
        var origin = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = origin });
        var bot = runtime.Bots.GetBot(-1)!;
        runtime.RegisterParticipant(bot.Player);
        bot.Player.Orbs.AddItem(107000020);
        var monsterArea = AreaType.S2Library1;
        var monsterCell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, monsterArea);
        runtime.Monsters.Entities[1] = new Monster
        {
            MonsterId = 1, Alive = true, Health = 100, Area = monsterArea,
            Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, monsterCell)
        };
        var service = new BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance);

        var selected = service.SelectMovementTarget(runtime, bot, DateTime.UtcNow);

        Assert.NotNull(selected);
        Assert.NotEqual(monsterCell, selected);
        Assert.Equal(area, GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, selected!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RecentAttackerTriggersEscapeWhenDangerousOtherwiseWander(bool stronger, bool wounded)
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

        var selected = service.SelectMovementTarget(runtime, bot, DateTime.UtcNow);

        Assert.NotNull(selected);
        if (stronger || wounded)
        {
            Assert.NotEqual(targetCell, selected);
            Assert.True(selected!.GetDistance(targetCell) >= origin.GetDistance(targetCell));
        }
        else
        {
            Assert.True(bot.ExplorationTarget.HasValue);
            Assert.Equal(bot.ExplorationTarget.Value.Cell, selected);
        }
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
