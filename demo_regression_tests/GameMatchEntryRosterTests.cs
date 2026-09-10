using game_server.matches;
using game_server.matches.entry;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class GameMatchEntryRosterTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(5)]
    [InlineData(0)]
    public async Task PrepareMatchAsync_AllocatesUniqueNegativeBotIdsAcrossMatches(int bots)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var redis = new InMemoryRedisOperations();
        var logger = new RecordingLogger();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(logger);
        var service = TestGameSessionServices.CreateEntryService(redis, store, logger);
        await new PlayerInfo(101, false) { Name = "Human" }.Save(redis);

        async Task<List<long>> Prepare(long matchingId)
        {
            await redis.HashSetAsync(network.common.MatchingRedisKeys.Key(matchingId),
                network.common.MatchingRedisKeys.ManifestField,
                MessagePack.MessagePackSerializer.Serialize(new MatchManifest { HumanPlayerIds = [101], BotCount = bots }));
            await redis.HashSetAsync(network.common.MatchingRedisKeys.Key(matchingId),
                network.common.MatchingRedisKeys.EntryReadyField,
                new byte[] { network.common.MatchingRedisKeys.EntryReadyValue });
            await redis.StringSetAsync(network.common.MatchingRedisKeys.ReservationKey(101),
                matchingId, TimeSpan.FromMinutes(2));
            var runtime = store.GetOrCreate(matchingId);
            await service.PrepareMatchAsync(matchingId, runtime);
            return runtime.GetPlayerProfiles().Where(player => player.PlayerId < 0).Select(player => player.PlayerId).ToList();
        }

        var ids = await Prepare(981030);
        var otherIds = await Prepare(981031);
        Assert.Equal(bots, ids.Count);
        Assert.Equal(bots, otherIds.Count);
        Assert.All(ids, id => Assert.True(id < 0));
        Assert.Equal(bots, ids.Distinct().Count());
        Assert.Empty(ids.Intersect(otherIds));
    }
    // ID 구성은 발행 측의 계약이다. 소비 측에서는 인원수 검증 후 entry_ready를 확인한다.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public async Task PrepareMatchAsync_RequiresEntryReadyForValidParticipantCounts(int humanCount)
    {
        var redis = new InMemoryRedisOperations();
        var logger = new RecordingLogger();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(logger);
        var service = TestGameSessionServices.CreateEntryService(redis, store, logger);
        var runtime = store.GetOrCreate(981021);
        var manifest = new MatchManifest
        {
            HumanPlayerIds = Enumerable.Range(1, humanCount).Select(id => (long)id).ToList(),
            BotCount = 8 - humanCount
        };
        await redis.HashSetAsync(network.common.MatchingRedisKeys.Key(runtime.MatchingId),
            network.common.MatchingRedisKeys.ManifestField, MessagePack.MessagePackSerializer.Serialize(manifest));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareMatchAsync(runtime.MatchingId, runtime));
        Assert.Equal($"Matching handoff was not committed for match {runtime.MatchingId}.", error.Message);
        Assert.False(runtime.IsSetupComplete);
        Assert.Empty(runtime.GetPlayerProfiles());
        Assert.Equal(1, runtime.EntryInitializationLock.CurrentCount);
    }
    public static IEnumerable<object[]> InvalidParticipantCounts()
    {
        yield return [new MatchManifest { HumanPlayerIds = [1], BotCount = -1 }];
        yield return [new MatchManifest { HumanPlayerIds = [1], BotCount = 8 }];
        yield return [new MatchManifest { HumanPlayerIds = [1], BotCount = int.MaxValue }];
        yield return [new MatchManifest { BotCount = 8 }];
    }

    [Theory]
    [MemberData(nameof(InvalidParticipantCounts))]
    public async Task PrepareMatchAsync_RejectsInvalidParticipantCountsBeforeCheckingEntryReady(MatchManifest manifest)
    {
        var redis = new InMemoryRedisOperations();
        var logger = new RecordingLogger();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(logger);
        var service = TestGameSessionServices.CreateEntryService(redis, store, logger);
        var runtime = store.GetOrCreate(981020);
        await redis.HashSetAsync(network.common.MatchingRedisKeys.Key(runtime.MatchingId),
            network.common.MatchingRedisKeys.ManifestField, MessagePack.MessagePackSerializer.Serialize(manifest));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareMatchAsync(runtime.MatchingId, runtime));
        Assert.Equal("Invalid match participant count.", error.Message);
        Assert.False(runtime.IsSetupComplete);
        Assert.Equal(1, runtime.EntryInitializationLock.CurrentCount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareMatchAsync_LoadsHumanAndBotProfilesOrRejectsMissingHuman(bool missingHuman)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var redis = new InMemoryRedisOperations();
        var logger = new RecordingLogger();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(logger);
        var service = TestGameSessionServices.CreateEntryService(redis, store, logger);
        var runtime = store.GetOrCreate(981032);
        if (!missingHuman)
            await new PlayerInfo(101, false) { Name = "Human", WearItemIdList = [123] }.Save(redis);
        await redis.HashSetAsync(network.common.MatchingRedisKeys.Key(runtime.MatchingId),
            network.common.MatchingRedisKeys.ManifestField,
            MessagePack.MessagePackSerializer.Serialize(new MatchManifest { HumanPlayerIds = [101], BotCount = 1 }));
        await redis.HashSetAsync(network.common.MatchingRedisKeys.Key(runtime.MatchingId),
            network.common.MatchingRedisKeys.EntryReadyField,
            new byte[] { network.common.MatchingRedisKeys.EntryReadyValue });
        await redis.StringSetAsync(network.common.MatchingRedisKeys.ReservationKey(101), runtime.MatchingId, TimeSpan.FromMinutes(2));
        if (missingHuman)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareMatchAsync(runtime.MatchingId, runtime));
            Assert.False(runtime.IsSetupComplete);
            Assert.Equal(1, runtime.EntryInitializationLock.CurrentCount);
            return;
        }
        await service.PrepareMatchAsync(runtime.MatchingId, runtime);
        Assert.Equal(2, runtime.GetPlayerProfiles().Count);
        var human = Assert.Single(runtime.GetPlayerProfiles(), player => player.PlayerId == 101);
        Assert.Equal("Human", human.Name);
        Assert.Equal(new[] { 123 }, human.WearItemIdList);
        var bot = Assert.Single(runtime.GetPlayerProfiles(), player => player.PlayerId < 0);
        var expectedBot = runtime.Bots.GetPlayerProfile(runtime.MatchingId, bot.PlayerId)!;
        Assert.Equal(expectedBot.Name, bot.Name);
        Assert.Equal(expectedBot.WearItemIdList, bot.WearItemIdList);
    }
}
