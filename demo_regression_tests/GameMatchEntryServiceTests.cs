using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class GameMatchEntryServiceTests
{
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
        return (new GameMatchEntryService(redis, store, GameServerDevOptions.Disabled, NullLogger.Instance),
            redis, store, store.GetOrCreate(id));
    }

    private static async Task Seed(InMemoryRedisOperations redis, long id)
    {
        await redis.HashSetAsync(MatchingRedisKeys.Key(id), MatchingRedisKeys.ManifestField,
            MessagePackSerializer.Serialize(new MatchManifest { HumanPlayerIds = [1001], BotCount = 1, Mode = MatchMode.Normal }));
        await redis.HashSetAsync(MatchingRedisKeys.Key(id), MatchingRedisKeys.EntryReadyField,
            new byte[] { MatchingRedisKeys.EntryReadyValue });
        await redis.StringSetAsync(MatchingRedisKeys.ReservationKey(1001), id, TimeSpan.FromMinutes(2));
    }
}
