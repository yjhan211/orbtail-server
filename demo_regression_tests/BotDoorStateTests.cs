using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotDoorStateTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void DoorSelectionRequiresRegisteredCell(int cellOffset, bool canOpen)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982013);
        using var scope = runtime.Enter();
        var door = GameDoorData.Get(213)!;
        var cell = new Cell((int)door.PositionX, (int)door.PositionY);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        bot.Player.CurrentArea = door.AreaType;
        foreach (var otherDoor in GameDoorData.GetByAreaType(door.AreaType))
        {
            if (otherDoor.DoorId != door.DoorId)
            {
                runtime.Doors.OpenDoor(otherDoor.DoorId);
            }
        }
        bot.Player.Cell = new Cell(cell.X + cellOffset, cell.Y);
        bot.Player.GameInfo.ObjectInfo.Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, bot.Player.Cell);
        var service = new BotBehaviorService(null!, null!, new PlayerInteractionService(), NullLogger<BotBehaviorService>.Instance);

        service.ProcessDoorInteractions(runtime, [bot], [], DateTime.UtcNow);

        Assert.Equal(canOpen, bot.Player.PendingDoorInteractionId == 702000113);
        Assert.Equal(canOpen ? PlayerState.EXPLORE_1 : PlayerState.IDLE, bot.Player.State);
    }

    [Fact]
    public void DamageInterruptionReturnsToIdleThenRestartsWhenStillAtDoor()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982012);
        using (runtime.Enter())
        {
            var door = GameDoorData.Get(213)!;
            var cell = new Cell((int)door.PositionX, (int)door.PositionY);
            runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
            var bot = runtime.Bots.GetBot(-1)!;
            bot.Player.CurrentArea = door.AreaType;
            bot.Player.CompleteDoor(); // 첫 문은 피격 중단 보호 대상이다.
            bot.Player.BeginDoor(702000113, 0);
            bot.Player.State = PlayerState.EXPLORE_1;
            var now = DateTime.UtcNow;
            new game_server.matches.MatchCombatDamageService(null!).RecordCombatContact(runtime, bot.Player, 1, now);

            Assert.Equal(PlayerState.IDLE, bot.Player.State);
            Assert.Null(bot.Player.PendingDoorInteractionId);
            var service = new BotBehaviorService(null!, null!, new PlayerInteractionService(), NullLogger<BotBehaviorService>.Instance);
            service.ProcessDoorInteractions(runtime, [bot], [], now.AddMilliseconds(100));
            Assert.Equal(PlayerState.EXPLORE_1, bot.Player.State);
            Assert.Equal(702000113, bot.Player.PendingDoorInteractionId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoorOpeningChangesActionState(bool openedByAnotherPlayer)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982011);
        using (runtime.Enter())
        {
            var door = GameDoorData.Get(213)!;
            var cell = new Cell((int)door.PositionX, (int)door.PositionY);
            runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
            var bot = runtime.Bots.GetBot(-1)!;
            bot.Player.CurrentArea = door.AreaType;
            var service = new BotBehaviorService(null!, null!, new PlayerInteractionService(), NullLogger<BotBehaviorService>.Instance);
            var now = DateTime.UtcNow;

            service.ProcessDoorInteractions(runtime, [bot], [], now);
            Assert.Equal(PlayerState.EXPLORE_1, bot.Player.State);
            Assert.Equal(702000113, bot.Player.PendingDoorInteractionId);

            service.ProcessDoorInteractions(runtime, [bot], [], now.AddMilliseconds(100));
            Assert.Equal(PlayerState.EXPLORE_1, bot.Player.State);

            if (openedByAnotherPlayer)
                runtime.Doors.OpenDoor(213);
            service.ProcessDoorInteractions(runtime, [bot], [], now.AddSeconds(Config.GetSwarmDoorGaugeSeconds(213) + 1));
            Assert.Equal(PlayerState.IDLE, bot.Player.State);
            Assert.Null(bot.Player.PendingDoorInteractionId);
        }
    }
}
