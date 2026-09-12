using game_server.matches;
using game_server.matches.logging;
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
        var bot = new BotPlayerState { PlayerId = -1 };

        Assert.Equal(1f, BotPlayerManager.GetBotMovementSpeedMultiplier(bot));
        Assert.True(bot.Player.Orbs.TryAddItemWithCapacity(107000020, 8, out _));
        Assert.Equal(1.06f, BotPlayerManager.GetBotMovementSpeedMultiplier(bot));
        Assert.True(bot.Player.Orbs.TryAddItemWithCapacity(107000022, 8, out _));
        Assert.Equal(1.08f, BotPlayerManager.GetBotMovementSpeedMultiplier(bot));

        bot.BootsSpeedUntilUtc = DateTime.UtcNow.AddMinutes(1);
        Assert.Equal(1.08f * Config.BOOTS_MOVE_SPEED_MULTIPLIER,
            BotPlayerManager.GetBotMovementSpeedMultiplier(bot));
        bot.Player.Orbs.TakeAllItems();
        Assert.Equal(Config.BOOTS_MOVE_SPEED_MULTIPLIER,
            BotPlayerManager.GetBotMovementSpeedMultiplier(bot));
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
            match.Bots.RegisterBots(Config.SWARM_MATCH_MAP, [-1L], cells);
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
            var spatial = match.Bots.SynthesizeGameObjectInfo(-1)!;
            var snapshot = SwarmBotPlayerInfoSnapshot.Capture(profile, spatial);
            Assert.Equal(PlayerState.SLEEP, snapshot.ToGameObjectInfo().State);
        }
    }
    [Fact]
    public void BotMovementPlanIsBoundToItsMatchAndRejectsReleasedWork()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        Assert.NotSame(first.Bots, second.Bots);
        using (MatchRuntimeStore.Enter(first))
            Assert.Equal(1L, first.Bots.PrepareMovementTick(first.Closures, first.GroundItems, logs, [], [], _ => default).MatchingId);
        using (MatchRuntimeStore.Enter(second))
            Assert.Equal(2L, second.Bots.PrepareMovementTick(second.Closures, second.GroundItems, logs, [], [], _ => default).MatchingId);
        using (MatchRuntimeStore.Enter(first))
        {
            first.TryMarkEnded();
            var movementService = new BotMovementService(logs, NullLogger<BotMovementService>.Instance);
            Assert.Throws<InvalidOperationException>(() => movementService.ProcessTick(first, _ => default));
        }
        Assert.Throws<InvalidOperationException>(() => first.Bots.PrepareMovementTick(first.Closures, first.GroundItems, logs, [], [], _ => default));
        using (MatchRuntimeStore.Enter(second))
            Assert.Equal(2L, second.Bots.PrepareMovementTick(second.Closures, second.GroundItems, logs, [], [], _ => default).MatchingId);
    }

    [Fact]
    public void MonstersAreIsolatedAndReleasedWithTheirMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        Assert.NotSame(first.Monsters, second.Monsters);
        var monsters = new MatchMonsterService(new MonsterMovementService());
        using (MatchRuntimeStore.Enter(first))
        {
            Assert.True(monsters.Initialize(first, DateTime.UtcNow));
            Assert.False(monsters.Initialize(first, DateTime.UtcNow));
        }
        Assert.False(second.Monsters.IsInitialized);
        using (MatchRuntimeStore.Enter(second)) Assert.True(monsters.Initialize(second, DateTime.UtcNow));
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
            match.Bots.RegisterBots(Config.SWARM_MATCH_MAP, [-1L], cells);
            var bot = Assert.IsType<BotPlayerState>(match.Bots.GetBot(-1));
            var profile = match.Bots.GetPlayerProfile(-1)!;
            bot.Player.ApplyDamage(20);
            match.InitializeMatch(MatchMode.Normal, cells, [new PlayerInfo { PlayerId = 1 }, profile]);
            Assert.Same(bot.Player, match.GetParticipant(-1));
            Assert.Same(profile, bot.Player.Profile);
            Assert.Null(typeof(BotPlayerState).GetProperty("Health"));
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
            match.Bots.RegisterBots(Config.SWARM_MATCH_MAP, [-1L], cells);
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
            var snapshot = match.Bots.SynthesizeGameObjectInfo(-1)!;
            Assert.Equal(12, snapshot.Position.X);
            Assert.Equal(34, snapshot.Position.Y);
            Assert.Equal(180, snapshot.Rotation);
            Assert.Equal(3, bot.Player.Cell!.X);
            Assert.Equal(6, bot.Player.Velocity.X);
            Assert.Equal(Config.SWARM_MATCH_GROUND_AREA, bot.Player.CurrentArea);
            Assert.NotSame(player.Position, snapshot.Position);
            Assert.Null(match.GetParticipant(1)!.Position);
            foreach (string field in new[] { "Position", "Cell", "WalkVelocity", "Rotation", "CurrentArea" })
                Assert.Null(typeof(BotPlayerState).GetProperty(field));
        }
    }
    [Fact]
    public void BotsAreIsolatedAndReleasedWithTheirMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        first.Bots.RegisterBots(Config.SWARM_MATCH_MAP, [], new Dictionary<long, Cell>());
        first.Bots.GetBots().Add(new BotPlayerState { PlayerId = -1 });
        Assert.NotSame(first.Bots, second.Bots);
        Assert.Single(first.Bots.GetBots());
        Assert.Empty(second.Bots.GetBots());
        using (MatchRuntimeStore.Enter(first)) first.TryMarkEnded();
        Assert.Null(store.GetOrNull(1));
        Assert.Empty(first.Bots.GetBots());
        Assert.Same(second, store.GetOrNull(2));
    }
}
