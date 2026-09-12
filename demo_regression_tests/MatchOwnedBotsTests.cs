using game_server.matches;
using game_server.matches.monsters;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchOwnedBotsTests
{
    [Fact]
    public void BotMovementUsesCurrentWindOrbs()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var bot = new Bot { PlayerId = -1 };

        Assert.Equal(1f, BotMovementService.GetBotMovementSpeedMultiplier(bot));
        Assert.True(bot.Player.Orbs.TryAddItemWithCapacity(107000020, 8, out _));
        Assert.Equal(1.06f, BotMovementService.GetBotMovementSpeedMultiplier(bot));
        Assert.True(bot.Player.Orbs.TryAddItemWithCapacity(107000022, 8, out _));
        Assert.Equal(1.08f, BotMovementService.GetBotMovementSpeedMultiplier(bot));

        bot.BootsSpeedUntilUtc = DateTime.UtcNow.AddMinutes(1);
        Assert.Equal(1.08f * Config.BOOTS_MOVE_SPEED_MULTIPLIER,
            BotMovementService.GetBotMovementSpeedMultiplier(bot));
        bot.Player.Orbs.TakeAllItems();
        Assert.Equal(Config.BOOTS_MOVE_SPEED_MULTIPLIER,
            BotMovementService.GetBotMovementSpeedMultiplier(bot));
    }

    [Fact]
    public void ProfileLookupDoesNotReinitializeBotProfile()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948022);
        using (match.Enter())
        {
            var cells = network.common.data.MatchSpawnData.CreatePhaseRoomAssignments(match.MatchingId, [1L, -1L]);
            match.Bots.RegisterBots(match.MatchingId, [-1L], cells);
            var bot = match.Bots.GetBot(-1)!;
            var profile = bot.Player.Profile;
            Assert.Equal("Player1", profile.Name);
            Assert.NotEmpty(profile.WearItemIdList);
            var wearItems = profile.WearItemIdList;
            profile.Name = "Updated";
            profile.Hp = 17;
            bot.Player.State = PlayerState.SLEEP;
            Assert.Same(profile, match.Bots.GetPlayerProfile(-1));
            Assert.Same(wearItems, profile.WearItemIdList);
            Assert.Equal("Updated", profile.Name);
            Assert.Equal(17, profile.Hp);
            Assert.Equal(PlayerState.IDLE, profile.State);
            var spatial = match.Bots.SynthesizeGameObjectInfo(match.MatchingId, -1)!;

            Assert.Equal(PlayerState.SLEEP, spatial.State);
        }
    }
    [Fact]
    public void BotMovementIsBoundToItsMatchAndRejectsReleasedWork()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        Assert.NotSame(first.Bots, second.Bots);
        var movementService = new BotMovementService(NullLogger<BotMovementService>.Instance);
        using (MatchRuntimeStore.Enter(first))
            movementService.ProcessTick(first, _ => default);
        using (MatchRuntimeStore.Enter(second))
            movementService.ProcessTick(second, _ => default);
        using (MatchRuntimeStore.Enter(first))
        {
            first.TryMarkEnded();

            Assert.Throws<InvalidOperationException>(() => movementService.ProcessTick(first, _ => default));
        }
        Assert.Throws<InvalidOperationException>(() => movementService.ProcessTick(first, _ => default));
        using (MatchRuntimeStore.Enter(second))
            movementService.ProcessTick(second, _ => default);
    }

    [Fact]
    public void SharedMovementServiceKeepsPlanningOrderPerMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(101);
        var second = store.GetOrCreate(102);
        var service = new BotMovementService(NullLogger<BotMovementService>.Instance);
        foreach (var match in new[] { first, second })
        {
            using (match.Enter())
            {
                match.Bots.GetBots().Add(new Bot { PlayerId = -1 });
                match.Bots.GetBots().Add(new Bot { PlayerId = -2 });
            }
        }

        long SelectNext(MatchRuntime match)
        {
            using (match.Enter())
            {
                return service.ProcessBotMovementTick(match,
                    new Dictionary<long, AreaType>(), _ => default).PlanningBotId;
            }
        }

        Assert.Throws<InvalidOperationException>(() => service.ProcessTick(first, _ => default));
        Assert.Equal(-2L, SelectNext(first));
        Assert.Equal(-2L, SelectNext(second));
        Assert.Equal(-1L, SelectNext(first));
        Assert.Equal(-1L, SelectNext(second));
    }
    [Fact]
    public void MonstersAreIsolatedAndReleasedWithTheirMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        Assert.NotSame(first.Monsters, second.Monsters);
        using (MatchRuntimeStore.Enter(first))
        {
            Assert.True(first.Monsters.Initialize(DateTime.UtcNow));
            Assert.False(first.Monsters.Initialize(DateTime.UtcNow));
        }
        Assert.False(second.Monsters.IsInitialized);
        using (MatchRuntimeStore.Enter(second)) Assert.True(second.Monsters.Initialize(DateTime.UtcNow));
        using (MatchRuntimeStore.Enter(first)) first.TryMarkEnded();
        Assert.False(first.Monsters.IsInitialized);
        Assert.True(second.Monsters.IsInitialized);
    }

    [Fact]
    public void SetupKeepsTheBotAndRosterOnTheSamePlayerState()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948020);
        using (match.Enter())
        {
            var cells = network.common.data.MatchSpawnData.CreatePhaseRoomAssignments(match.MatchingId, [1L, -1L]);
            match.Bots.RegisterBots(match.MatchingId, [-1L], cells);
            var bot = Assert.IsType<Bot>(match.Bots.GetBot(-1));
            var profile = match.Bots.GetPlayerProfile(-1)!;
            bot.Player.ApplyDamage(20);
            match.InitializeMatch(MatchMode.Normal, cells, [new PlayerInfo { PlayerId = 1 }, profile]);
            Assert.Same(bot.Player, match.GetParticipant(-1));
            Assert.Same(profile, bot.Player.Profile);
            Assert.Null(typeof(Bot).GetProperty("Health"));
            Assert.Equal(Config.MAX_HEALTH - 20, match.GetParticipant(-1)!.Health);
            match.GetParticipant(-1)!.Recover(5);
            Assert.Equal(Config.MAX_HEALTH - 15, bot.Player.Health);
            Assert.Equal(Config.MAX_HEALTH, match.GetParticipant(1)!.Health);
            Assert.Null(bot.Player.Session);
        }
    }
    [Fact]
    public void BotSpatialStateIsSharedWithTheRosterAndPublishedObject()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948021);
        using (match.Enter())
        {
            var cells = network.common.data.MatchSpawnData.CreatePhaseRoomAssignments(match.MatchingId, [1L, -1L]);
            match.Bots.RegisterBots(match.MatchingId, [-1L], cells);
            var bot = match.Bots.GetBot(-1)!;
            var profile = match.Bots.GetPlayerProfile(-1)!;
            match.InitializeMatch(MatchMode.Normal, cells, [new PlayerInfo { PlayerId = 1 }, profile]);
            var player = match.GetParticipant(-1)!;
            Assert.Same(bot.Player, player);
            Assert.Equal(cells[-1].X, player.Cell!.X);
            Assert.Equal(cells[-1].Y, player.Cell.Y);
            Assert.NotNull(player.Position);
            Assert.NotEqual(AreaType.None, player.CurrentArea);

            player.Position = new Vector3f(12, 34, 0);
            player.Cell = new Cell(3, 4);
            player.Velocity = new Vector3f(6, 0, 0);
            player.Rotation = 180;
            player.CurrentArea = Config.SWARM_MATCH_GROUND_AREA;
            var snapshot = match.Bots.SynthesizeGameObjectInfo(match.MatchingId, -1)!;
            Assert.Equal(12, snapshot.Position.X);
            Assert.Equal(34, snapshot.Position.Y);
            Assert.Equal(180, snapshot.Rotation);
            Assert.Equal(3, bot.Player.Cell!.X);
            Assert.Equal(6, bot.Player.Velocity.X);
            Assert.Equal(Config.SWARM_MATCH_GROUND_AREA, bot.Player.CurrentArea);
            Assert.NotSame(player.Position, snapshot.Position);
            Assert.Null(match.GetParticipant(1)!.Position);
            foreach (string field in new[] { "Position", "Cell", "WalkVelocity", "Rotation", "CurrentArea" })
                Assert.Null(typeof(Bot).GetProperty(field));
        }
    }
    [Fact]
    public void BotsAreIsolatedAndReleasedWithTheirMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        first.Bots.RegisterBots(first.MatchingId, [], new Dictionary<long, Cell>());
        Assert.False(first.Bots.HasBots());
        first.Bots.GetBots().Add(new Bot { PlayerId = -1 });
        Assert.True(first.Bots.HasBots());
        Assert.NotSame(first.Bots, second.Bots);
        var movementService = new BotMovementService(NullLogger<BotMovementService>.Instance);
        Assert.Single(first.Bots.GetBots());
        Assert.Empty(second.Bots.GetBots());
        using (MatchRuntimeStore.Enter(first)) first.TryMarkEnded();
        Assert.Null(store.GetOrNull(1));
        Assert.Empty(first.Bots.GetBots());
        Assert.False(first.Bots.HasBots());
        Assert.Same(second, store.GetOrNull(2));
    }
}
