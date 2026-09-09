using game_server;
using game_server.logging;
using game_server.matches.entry;
using game_server.matches;
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
        await service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        long botId = Assert.Single(runtime.PlayerRoster, player => player.PlayerId < 0).PlayerId;
        var bot = Assert.Single(runtime.PlayerRoster, profile => profile.PlayerId == botId);
        Assert.False(string.IsNullOrWhiteSpace(bot.Name));
        Assert.NotEmpty(bot.WearItemIdList);
        Assert.Equal(runtime.Bots.SynthesizePlayerInfo(runtime.MatchingId, botId)!.WearItemIdList, bot.WearItemIdList);

        var restored = MessagePackSerializer.Deserialize<G_TO_C_MATCH_ROSTER>(
            MessagePackSerializer.Serialize(new G_TO_C_MATCH_ROSTER
            {
                MatchingId = runtime.MatchingId,
                PlayerRoster = runtime.PlayerRoster.ToList()
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
        await service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        int[] initialDoors = GameDoorData.GetAll().Where(door => door.IsInitiallyOpen)
            .Select(door => door.DoorId).Order().ToArray();
        Assert.Equal(initialDoors, runtime.Doors.GetOpenDoors().Order().ToArray());

        using (runtime.Enter())
        {
            runtime.Doors.CloseDoorsForAreas(GameDoorData.GetAll().Select(door => door.AreaType));
            runtime.Doors.OpenDoor(990013);
        }
        await service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        Assert.Equal(new[] { 990013 }, runtime.Doors.GetOpenDoors());
    }

    [Fact]
    public async Task MissingHumanProfileDoesNotCommitCompositionAndReleasesInitializationLock()
    {
        var (service, redis, store, runtime) = await Prepare(981012);
        await redis.HashDeleteAsync(PlayerInfo.HashKey, "1001");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime));
        Assert.False(runtime.IsSetupComplete);
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

        await service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        var initialRoster = runtime.PlayerRoster;
        profile.Name = "NextMatch";
        profile.WearItemIdList = [303];
        await profile.Save(redis);
        await service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);

        Assert.Same(initialRoster, runtime.PlayerRoster);
        var human = Assert.Single(runtime.PlayerRoster, entry => entry.PlayerId == 1001);
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
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var service = TestGameSessionServices.CreateEntryService(
            redis, store, NullLogger.Instance);
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
            new InMemoryRedisOperations(), TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance),
            NullLogger.Instance);

        Assert.Null(await service.ConsumeTicketAsync(ticket));
    }

    [Fact]
    public async Task ConcurrentEntriesShareOneCompositionAndBotRoster()
    {
        var (service, redis, store, runtime) = await Prepare(981001);
        await runtime.EntryInitializationLock.WaitAsync();
        Task first = service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        Task second = service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        runtime.EntryInitializationLock.Release();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(runtime.IsSetupComplete);
        Assert.Single(runtime.PlayerRoster, player => player.PlayerId < 0);
        Assert.Single(runtime.Bots.GetBots(runtime.MatchingId));
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var events = logs.GetRecent(runtime.MatchingId);
        Assert.Single(events, entry => entry.Type == "MATCH_STARTED");
        var spawn = Assert.Single(events, entry => entry.Type == "SPAWN_ASSIGNMENT");
        var bot = Assert.Single(runtime.Bots.GetBots(runtime.MatchingId));
        Assert.True(spawn.IsBot);
        Assert.Equal(bot.PlayerId, spawn.PlayerId);
        Assert.Equal(bot.Cell.X, spawn.CellX);
        Assert.Equal(bot.Cell.Y, spawn.CellY);

        await service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        Assert.Equal(events.Select(entry => entry.Seq), logs.GetRecent(runtime.MatchingId).Select(entry => entry.Seq));
    }

    [Fact]
    public async Task HumanOnlyCompositionAlsoStartsMatchLog()
    {
        var (service, redis, store, runtime) = await Prepare(981010);
        await redis.HashSetAsync(MatchingRedisKeys.Key(runtime.MatchingId), MatchingRedisKeys.ManifestField,
            MessagePackSerializer.Serialize(new MatchManifest { HumanPlayerIds = [1001], BotCount = 0, Mode = MatchMode.Normal }));
        await service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        Assert.Single(logs.GetRecent(runtime.MatchingId), entry => entry.Type == "MATCH_STARTED");
        Assert.DoesNotContain(logs.GetRecent(runtime.MatchingId), entry => entry.Type == "SPAWN_ASSIGNMENT");
    }

    [Fact]
    public async Task ConsumeTicketPropagatesStoreFailureToSession()
    {
        var redis = new InMemoryRedisOperations();
        var service = TestGameSessionServices.CreateEntryService(
            redis, TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance), NullLogger.Instance);
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
        await service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        await redis.StringSetAsync(MatchingRedisKeys.ReservationKey(1001), "another-match", TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime));
        Assert.True(runtime.IsSetupComplete);
        Assert.Equal(1, runtime.EntryInitializationLock.CurrentCount);
    }

    [Fact]
    public async Task WaitingEntryDoesNotRecreateTerminalMatch()
    {
        var (service, redis, store, runtime) = await Prepare(981003);
        await runtime.EntryInitializationLock.WaitAsync();
        Task pending = service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime);
        using (MatchRuntimeStore.Enter(runtime))
            Assert.True(runtime.TryMarkEnded());
        runtime.EntryInitializationLock.Release();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.False(runtime.IsSetupComplete);
        Assert.Null(store.GetOrNull(runtime.MatchingId));
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
            await service.PrepareMatchAsync(
                second.MatchingId, Config.SWARM_MATCH_MAP, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(second.IsSetupComplete);
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
            service.PrepareMatchAsync(runtime.MatchingId, Config.SWARM_MATCH_MAP, runtime));
        Assert.False(runtime.IsSetupComplete);
        Assert.Equal(1, runtime.EntryInitializationLock.CurrentCount);
    }

    private static async Task<(GameMatchEntryService, InMemoryRedisOperations, MatchRuntimeStore, MatchRuntime)> Prepare(long id)
    {
        var redis = new InMemoryRedisOperations();
        await Seed(redis, id);
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        return (TestGameSessionServices.CreateEntryService(redis, store, NullLogger.Instance),
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
