using game_server;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingEntryReadyFailsWithoutWaiting(bool canceled)
    {
        var (service, redis, _, runtime) = await Prepare(981016);
        await redis.HashDeleteAsync(MatchingRedisKeys.Key(runtime.MatchingId), MatchingRedisKeys.EntryReadyField);
        if (canceled)
            await redis.StringSetAsync(MatchingRedisKeys.EntryStateKey(runtime.MatchingId),
                MatchingRedisKeys.EntryCanceledState, TimeSpan.FromMinutes(2));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareMatchAsync(runtime)
                .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains(canceled ? "canceled" : "not committed", error.Message);
        Assert.False(runtime.IsSetupComplete);
        Assert.Equal(1, runtime.EntryInitializationLock.CurrentCount);
    }
    [Fact]
    public async Task SetupGrantsResourcesBeforeConnectionsAndLaterEntryDoesNotGrantAgain()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var (service, redis, store, runtime) = await Prepare(981015);
        await new PlayerInfo(1002, false) { Name = "LateHuman" }.Save(redis);
        await redis.HashSetAsync(MatchingRedisKeys.Key(runtime.MatchingId), MatchingRedisKeys.ManifestField,
            MessagePackSerializer.Serialize(new MatchManifest { HumanPlayerIds = [1001, 1002], BotCount = 1, Mode = MatchMode.Normal }));
        await redis.StringSetAsync(MatchingRedisKeys.ReservationKey(1002), runtime.MatchingId, TimeSpan.FromMinutes(2));

        await service.PrepareMatchAsync(runtime);

        Assert.Empty(runtime.GetSessions());
        int humanStones = Config.SWARM_STARTING_STONE_GRANT;
        for (int index = 0; index < Config.SWARM_STARTING_ORB_GRANT_COUNT; index++)
            humanStones += Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(index));
        Assert.Equal(humanStones, TestGameSessionServices.SummonStones(runtime, 1001).StoneCount);
        Assert.Equal(humanStones, TestGameSessionServices.SummonStones(runtime, 1002).StoneCount);
        Assert.Empty(TestGameSessionServices.Orbs(runtime, 1001).GetOrderedOrbs());
        long botId = Assert.Single(runtime.GetPlayerProfiles(), player => player.PlayerId < 0).PlayerId;
        Assert.Empty(TestGameSessionServices.Orbs(runtime, botId).GetOrderedOrbs());
        Assert.Equal(humanStones, TestGameSessionServices.SummonStones(runtime, botId).StoneCount);

        using (runtime.Enter())
        {
            Assert.True(TestGameSessionServices.SpendSummonStones(runtime, 1001, 1));
            Assert.Throws<InvalidOperationException>(() =>
                runtime.InitializeMatch(runtime.Mode, runtime.SpawnCells, runtime.GetPlayerProfiles()));
        }
        await service.PrepareMatchAsync(runtime);
        Assert.Equal(humanStones, TestGameSessionServices.SummonStones(runtime, botId).StoneCount);
        Assert.Equal(humanStones - 1, TestGameSessionServices.SummonStones(runtime, 1001).StoneCount);
        Assert.Equal(humanStones, TestGameSessionServices.SummonStones(runtime, 1002).StoneCount);
        Assert.Empty(TestGameSessionServices.Orbs(runtime, botId).GetOrderedOrbs());
    }

    [Fact]
    public async Task EntryRosterContainsBotAppearanceWithoutSeparateAppearancePacket()
    {
        var (service, redis, store, runtime) = await Prepare(981014);
        await service.PrepareMatchAsync(runtime);
        long botId = Assert.Single(runtime.GetPlayerProfiles(), player => player.PlayerId < 0).PlayerId;
        var bot = Assert.Single(runtime.GetPlayerProfiles(), profile => profile.PlayerId == botId);
        Assert.False(string.IsNullOrWhiteSpace(bot.Name));
        Assert.NotEmpty(bot.WearItemIdList);
        Assert.Equal(runtime.Bots.GetPlayerProfile(botId)!.WearItemIdList, bot.WearItemIdList);

        var restored = MessagePackSerializer.Deserialize<G_TO_C_MATCH_ROSTER>(
            MessagePackSerializer.Serialize(new G_TO_C_MATCH_ROSTER
            {
                MatchingId = runtime.MatchingId,
                PlayerRoster = runtime.GetPlayerProfiles().ToList()
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
        await service.PrepareMatchAsync(runtime);
        int[] initialDoors = GameDoorData.GetAll().Where(door => door.IsInitiallyOpen)
            .Select(door => door.DoorId).Order().ToArray();
        Assert.Equal(initialDoors, runtime.Doors.GetOpenDoors().Order().ToArray());

        using (runtime.Enter())
        {
            runtime.Doors.CloseDoorsForAreas(GameDoorData.GetAll().Select(door => door.AreaType));
            runtime.Doors.OpenDoor(990013);
        }
        await service.PrepareMatchAsync(runtime);
        Assert.Equal(new[] { 990013 }, runtime.Doors.GetOpenDoors());
    }

    [Fact]
    public async Task MissingHumanProfileDoesNotCommitCompositionAndReleasesInitializationLock()
    {
        var (service, redis, store, runtime) = await Prepare(981012);
        await redis.HashDeleteAsync(PlayerInfo.HashKey, "1001");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareMatchAsync(runtime));
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

        await service.PrepareMatchAsync(runtime);
        var initialRoster = runtime.GetPlayerProfiles();
        profile.Name = "NextMatch";
        profile.WearItemIdList = [303];
        await profile.Save(redis);
        await service.PrepareMatchAsync(runtime);

        Assert.Equal(initialRoster, runtime.GetPlayerProfiles());
        var human = Assert.Single(runtime.GetPlayerProfiles(), entry => entry.PlayerId == 1001);
        Assert.Equal("AtEntry", human.Name);
        Assert.Equal(new[] { 202 }, human.WearItemIdList);
        Assert.Equal(new[] { 202 }, runtime.GetParticipant(1001)!.Profile.WearItemIdList);
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
        Task first = service.PrepareMatchAsync(runtime);
        Task second = service.PrepareMatchAsync(runtime);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        runtime.EntryInitializationLock.Release();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(runtime.IsSetupComplete);
        Assert.Single(runtime.GetPlayerProfiles(), player => player.PlayerId < 0);
        Assert.Single(runtime.Bots.GetBots());
        var bot = Assert.Single(runtime.Bots.GetBots());
        Assert.NotNull(bot.Player.Cell);

        await service.PrepareMatchAsync(runtime);
        Assert.Single(runtime.Bots.GetBots());
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
        await service.PrepareMatchAsync(runtime);
        await redis.StringSetAsync(MatchingRedisKeys.ReservationKey(1001), "another-match", TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareMatchAsync(runtime));
        Assert.True(runtime.IsSetupComplete);
        Assert.Equal(1, runtime.EntryInitializationLock.CurrentCount);
    }

    [Fact]
    public async Task WaitingEntryDoesNotRecreateTerminalMatch()
    {
        var (service, redis, store, runtime) = await Prepare(981003);
        await runtime.EntryInitializationLock.WaitAsync();
        Task pending = service.PrepareMatchAsync(runtime);
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
            await service.PrepareMatchAsync(second).WaitAsync(TimeSpan.FromSeconds(5));
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
            service.PrepareMatchAsync(runtime));
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
