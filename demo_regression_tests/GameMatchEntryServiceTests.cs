using game_server.matches;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;
using network.gameentry;

namespace demo_regression_tests;

public sealed class GameMatchEntryServiceTests
{
    [Fact]
    public async Task EntryRosterContainsBotAppearanceWithoutSeparateAppearancePacket()
    {
        var (service, redis, store, runtime) = await Prepare(981014);
        var composition = await service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        long botId = Assert.Single(composition.BotPlayerIds);
        var bot = Assert.Single(composition.PlayerRoster, profile => profile.PlayerId == botId);
        Assert.False(string.IsNullOrWhiteSpace(bot.Name));
        Assert.NotEmpty(bot.WearItemIdList);
        Assert.Equal(runtime.Bots.SynthesizePlayerInfo(runtime.MatchingId, botId)!.WearItemIdList, bot.WearItemIdList);

        var restored = MessagePackSerializer.Deserialize<G_TO_C_MATCH_ROSTER>(
            MessagePackSerializer.Serialize(new G_TO_C_MATCH_ROSTER
            {
                MatchingId = runtime.MatchingId,
                PlayerRoster = composition.PlayerRoster.ToList()
            }));
        var restoredBot = Assert.Single(restored.PlayerRoster, profile => profile.PlayerId == botId);
        Assert.Equal(bot.Name, restoredBot.Name);
        Assert.Equal(bot.WearItemIdList, restoredBot.WearItemIdList);
        Assert.DoesNotContain("G_TO_C_PLAYER_APPEARANCE", Enum.GetNames<Protocol>());
    }

    [Fact]
    public async Task CompositionInitializesDoorsAndLaterEntryPreservesChanges()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var (service, redis, store, runtime) = await Prepare(981013);
        await service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        int[] initialDoors = GameDoorData.GetAll().Where(door => door.IsInitiallyOpen)
            .Select(door => door.DoorId).Order().ToArray();
        Assert.Equal(initialDoors, runtime.Doors.GetOpenDoors().Order().ToArray());

        using (runtime.Enter())
        {
            runtime.Doors.CloseDoorsForAreas(GameDoorData.GetAll().Select(door => door.AreaType));
            runtime.Doors.OpenDoor(990013);
        }
        await service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        Assert.Equal(new[] { 990013 }, runtime.Doors.GetOpenDoors());
    }

    [Fact]
    public async Task MissingHumanProfileDoesNotCommitCompositionAndReleasesInitializationLock()
    {
        var (service, redis, store, runtime) = await Prepare(981012);
        await redis.HashDeleteAsync(PlayerInfo.HashKey, "1001");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime));
        Assert.Null(runtime.Composition);
        Assert.Equal(1, runtime.EntryInitializationLock.CurrentCount);
    }

    [Fact]
    public async Task MatchAppearanceUsesLatestProfileAtCompositionAndRemainsFixedForLaterEntries()
    {
        var (service, redis, store, runtime) = await Prepare(981011);
        var profile = new PlayerInfo(1001, false) { Name = "Before", WearItemIdList = [101] };
        await profile.Save(redis);
        profile.Name = "AtEntry";
        profile.WearItemIdList = [202];
        await profile.Save(redis);

        var composition = await service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        profile.Name = "NextMatch";
        profile.WearItemIdList = [303];
        await profile.Save(redis);
        var laterEntry = await service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);

        Assert.Same(composition, laterEntry);
        var human = Assert.Single(laterEntry.PlayerRoster, entry => entry.PlayerId == 1001);
        Assert.Equal("AtEntry", human.Name);
        Assert.Equal(new[] { 202 }, human.WearItemIdList);
        Assert.Equal(new[] { 202 }, runtime.Roster.GetPlayerProfile(1001)!.WearItemIdList);
        Assert.Equal(new[] { 303 }, (await PlayerInfo.Load(redis, 1001))!.WearItemIdList);
    }

    [Theory]
    [InlineData("game-server-test", true)]
    [InlineData("another-node", false)]
    public async Task ConsumeTicketUsesCurrentNodeAndConsumesOnlyOnce(string targetNode, bool accepted)
    {
        var redis = new InMemoryRedisOperations();
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var service = TestGameSessionServices.CreateEntryService(
            redis, store, GameServerDevOptions.Disabled, NullLogger.Instance);
        var tickets = new GameEntryTicketService(new RedisGameEntryTicketStore(redis), new GameEntryTicketOptions());
        string ticket = await tickets.IssueAsync(new GameEntryContext
        {
            PlayerId = 1001, MatchingId = 981008, GameServerNodeId = targetNode
        });

        var context = await service.ConsumeTicketAsync(ticket);
        if (accepted)
        {
            Assert.NotNull(context);
            Assert.Equal(1001, context.PlayerId);
            Assert.Equal(981008, context.MatchingId);
        }
        else
        {
            Assert.Null(context);
        }

        Assert.Null(await service.ConsumeTicketAsync(ticket));
        Assert.Null(await tickets.ConsumeAsync(ticket, targetNode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid-ticket")]
    public async Task ConsumeTicketRejectsMissingOrMalformedTicket(string? ticket)
    {
        var service = TestGameSessionServices.CreateEntryService(
            new InMemoryRedisOperations(), new MatchRuntimeStore(NullLogger.Instance),
            GameServerDevOptions.Disabled, NullLogger.Instance);

        Assert.Null(await service.ConsumeTicketAsync(ticket));
    }

    [Fact]
    public async Task ConcurrentEntriesShareOneCompositionAndBotRoster()
    {
        var (service, redis, store, runtime) = await Prepare(981001);
        await runtime.EntryInitializationLock.WaitAsync();
        Task<MatchComposition> first = service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        Task<MatchComposition> second = service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        runtime.EntryInitializationLock.Release();

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(results[0], results[1]);
        Assert.Same(results[0], runtime.Composition);
        Assert.Single(results[0].BotPlayerIds);
        Assert.Single(runtime.Bots.GetBots(runtime.MatchingId));
        var logs = new GameEventLogManager(id => store.Get(id)?.EventLog);
        var events = logs.GetRecent(runtime.MatchingId);
        Assert.Single(events, entry => entry.Type == "MATCH_STARTED");
        var spawn = Assert.Single(events, entry => entry.Type == "SPAWN_ASSIGNMENT");
        var bot = Assert.Single(runtime.Bots.GetBots(runtime.MatchingId));
        Assert.True(spawn.IsBot);
        Assert.Equal(bot.PlayerId, spawn.PlayerId);
        Assert.Equal(bot.Cell.X, spawn.CellX);
        Assert.Equal(bot.Cell.Y, spawn.CellY);

        await service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        Assert.Equal(events.Select(entry => entry.Seq), logs.GetRecent(runtime.MatchingId).Select(entry => entry.Seq));
    }

    [Fact]
    public async Task HumanOnlyCompositionAlsoStartsMatchLog()
    {
        var (service, redis, store, runtime) = await Prepare(981010);
        await redis.HashSetAsync(MatchingRedisKeys.Key(runtime.MatchingId), MatchingRedisKeys.ManifestField,
            MessagePackSerializer.Serialize(new MatchManifest { HumanPlayerIds = [1001], BotCount = 0, Mode = MatchMode.Normal }));
        await service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        var logs = new GameEventLogManager(id => store.Get(id)?.EventLog);
        Assert.Single(logs.GetRecent(runtime.MatchingId), entry => entry.Type == "MATCH_STARTED");
        Assert.DoesNotContain(logs.GetRecent(runtime.MatchingId), entry => entry.Type == "SPAWN_ASSIGNMENT");
    }

    [Fact]
    public async Task ConsumeTicketPropagatesStoreFailureToSession()
    {
        var redis = new InMemoryRedisOperations();
        var service = TestGameSessionServices.CreateEntryService(
            redis, new MatchRuntimeStore(NullLogger.Instance), GameServerDevOptions.Disabled, NullLogger.Instance);
        var tickets = new GameEntryTicketService(new RedisGameEntryTicketStore(redis), new GameEntryTicketOptions());
        string ticket = await tickets.IssueAsync(new GameEntryContext
        {
            PlayerId = 1001, MatchingId = 981009, GameServerNodeId = "game-server-test"
        });
        redis.StringGetError = new IOException("Redis unavailable");

        await Assert.ThrowsAsync<IOException>(() => service.ConsumeTicketAsync(ticket));
    }

    [Fact]
    public async Task ExistingCompositionStillRequiresActiveReservation()
    {
        var (service, redis, store, runtime) = await Prepare(981002);
        var composition = await service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        await redis.StringSetAsync(MatchingRedisKeys.ReservationKey(1001), "another-match", TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime));
        Assert.Same(composition, runtime.Composition);
        Assert.Equal(1, runtime.EntryInitializationLock.CurrentCount);
    }

    [Fact]
    public async Task WaitingEntryDoesNotRecreateTerminalMatch()
    {
        var (service, redis, store, runtime) = await Prepare(981003);
        await runtime.EntryInitializationLock.WaitAsync();
        Task<MatchComposition> pending = service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        using (store.Enter(runtime))
            Assert.True(runtime.TryMarkTerminal());
        runtime.EntryInitializationLock.Release();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.Null(runtime.Composition);
        Assert.Null(store.Get(runtime.MatchingId));
        Assert.Equal(1, runtime.EntryInitializationLock.CurrentCount);
    }

    [Fact]
    public async Task InitializationLockIsPerMatch()
    {
        var (service, redis, store, first) = await Prepare(981004);
        await Seed(redis, 981005);
        var second = store.GetOrCreate(981005);
        await first.EntryInitializationLock.WaitAsync();
        try
        {
            var composition = await service.LoadCompositionAsync(
                second.MatchingId, Config.SWARM_MATCH_MAP, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(composition, second.Composition);
        }
        finally
        {
            first.EntryInitializationLock.Release();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedManifestReadReleasesInitializationLock(bool missing)
    {
        var (service, redis, store, runtime) = await Prepare(missing ? 981006 : 981007);
        if (missing)
            await redis.HashDeleteAsync(MatchingRedisKeys.Key(runtime.MatchingId), MatchingRedisKeys.ManifestField);
        else
            redis.HashGetError = new IOException("Redis unavailable");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.LoadCompositionAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime));
        Assert.Null(runtime.Composition);
        Assert.Equal(1, runtime.EntryInitializationLock.CurrentCount);
    }

    private static async Task<(GameMatchEntryService, InMemoryRedisOperations, MatchRuntimeStore, MatchRuntime)> Prepare(long id)
    {
        var redis = new InMemoryRedisOperations();
        await Seed(redis, id);
        var store = new MatchRuntimeStore(NullLogger.Instance);
        return (TestGameSessionServices.CreateEntryService(redis, store, GameServerDevOptions.Disabled, NullLogger.Instance),
            redis, store, store.GetOrCreate(id));
    }

    private static async Task Seed(InMemoryRedisOperations redis, long id)
    {
        await new PlayerInfo(1001, false) { Name = "Human" }.Save(redis);
        await redis.HashSetAsync(MatchingRedisKeys.Key(id), MatchingRedisKeys.ManifestField,
            MessagePackSerializer.Serialize(new MatchManifest { HumanPlayerIds = [1001], BotCount = 1, Mode = MatchMode.Normal }));
        await redis.HashSetAsync(MatchingRedisKeys.Key(id), MatchingRedisKeys.EntryReadyField,
            new byte[] { MatchingRedisKeys.EntryReadyValue });
        await redis.StringSetAsync(MatchingRedisKeys.ReservationKey(1001), id, TimeSpan.FromMinutes(2));
    }
}
