using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using game_server;
using game_server.matches;
using game_server.players;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;
using network.hosting;
using network.infrastructure.messaging;
using network.infrastructure.redis;


namespace server_tests;

public sealed class MatchEndLifecycleTests
{
    [Theory]
    [InlineData(MatchingLifecycleSubjects.PlayerLeft)]
    [InlineData(MatchingLifecycleSubjects.PlayerCompleted)]
    [InlineData(MatchingLifecycleSubjects.PlayerReleased)]
    public async Task SessionLifecycle_UsesExpectedSubjectAndPreservesDeferredCompletion(string subject)
    {
        var nats = new RecordingNatsClient();
        var service = CreateLifecycleService(nats, new RecordingLogger<GameServer>());
        IMatchSessionCleanup lifecycle = service;
        const long playerId = 101;
        const long matchingId = 42003;

        switch (subject)
        {
            case MatchingLifecycleSubjects.PlayerLeft:
                lifecycle.PublishPlayerLeft(playerId, matchingId);
                lifecycle.PublishPlayerLeft(playerId, matchingId);
                break;
            case MatchingLifecycleSubjects.PlayerCompleted:
                Action dispatch = Assert.IsType<Action>(lifecycle.PrepareGameCompletion(playerId, matchingId));
                Assert.Equal(0, nats.PublishCount);
                dispatch();
                dispatch();
                break;
            case MatchingLifecycleSubjects.PlayerReleased:
                lifecycle.ReleaseMatchingReservation(playerId, matchingId);
                lifecycle.ReleaseMatchingReservation(playerId, matchingId);
                break;
        }

        await service.DrainAsync();

        Assert.Equal(1, nats.PublishCount);
        Assert.Equal(subject, nats.LastSubject);
        var message = MessagePackSerializer.Deserialize<G_TO_U_MATCHING_LIFECYCLE>(nats.LastPayload!);
        Assert.Equal(playerId, message.PlayerId);
        Assert.Equal(matchingId, message.MatchingId);
    }

    [Fact]
    public async Task LifecycleCompletion_PrepareDefersOneShotCorePublish()
    {
        var nats = new RecordingNatsClient();
        MatchSessionCleanupService server = CreateLifecycleService(nats, new RecordingLogger<GameServer>());
        const long matchingId = 42_003;

        Action dispatch = Assert.IsType<Action>(server.PrepareNotification(
            MatchingLifecycleSubjects.PlayerCompleted,
            101L,
            matchingId));
        Assert.Equal(0, nats.PublishCount);

        dispatch();
        dispatch();
        await WaitForPendingMatchingRedisCleanupsAsync(server);

        Assert.Equal(1, nats.PublishCount);
        Assert.Equal(MatchingLifecycleSubjects.PlayerCompleted, nats.LastSubject);
        byte[] payload = Assert.IsType<byte[]>(nats.LastPayload);
        var message = MessagePackSerializer.Deserialize<G_TO_U_MATCHING_LIFECYCLE>(payload);
        Assert.Equal(101L, message.PlayerId);
        Assert.Equal(matchingId, message.MatchingId);
    }

    [Fact]
    public async Task LifecyclePublishFailure_IsLoggedAndLaterPublicationProgresses()
    {
        var nats = new RecordingNatsClient
        {
            PublishException = new InvalidOperationException("core failure")
        };
        var logger = new RecordingLogger<GameServer>();
        MatchSessionCleanupService server = CreateLifecycleService(nats, logger);

        server.Publish(
            MatchingLifecycleSubjects.PlayerLeft,
            102L,
            42_004L);
        await WaitForPendingMatchingRedisCleanupsAsync(server);

        Assert.Equal(1, nats.PublishCount);
        Assert.True(
            logger.Contains(
                LogLevel.Error,
                "Matching lifecycle publish failed:"));

        nats.PublishException = null;
        server.Publish(
            MatchingLifecycleSubjects.PlayerLeft,
            103L,
            42_004L);
        await WaitForPendingMatchingRedisCleanupsAsync(server);

        Assert.Equal(2, nats.PublishCount);
    }

    [Fact]
    public async Task LifecyclePublishFailure_StillReleasesExactReservation()
    {
        const long playerId = 1201;
        const long matchingId = 42_104;
        var redis = new InMemoryRedisOperations();
        await redis.StringSetAsync(
            MatchingRedisKeys.ReservationKey(playerId),
            matchingId,
            MatchingRedisKeys.PostEntryReservationLifetime);
        var nats = new RecordingNatsClient
        {
            PublishException = new InvalidOperationException("core failure")
        };
        var logger = new RecordingLogger<GameServer>();
        MatchSessionCleanupService server = CreateLifecycleService(nats, logger, redis);

        server.Publish(
            MatchingLifecycleSubjects.PlayerCompleted,
            playerId,
            matchingId);
        await WaitForPendingMatchingRedisCleanupsAsync(server);

        Assert.Null(redis.GetString(MatchingRedisKeys.ReservationKey(playerId)));
        Assert.Equal(1, nats.PublishCount);
        Assert.True(logger.Contains(LogLevel.Error, "Matching lifecycle publish failed:"));
    }

    [Fact]
    public async Task LifecycleReservationRelease_DoesNotDeleteDifferentReservation()
    {
        const long playerId = 1202;
        const long endedMatchingId = 42_105;
        const long newerMatchingId = 42_106;
        var redis = new InMemoryRedisOperations();
        await redis.StringSetAsync(
            MatchingRedisKeys.ReservationKey(playerId),
            newerMatchingId,
            MatchingRedisKeys.PostEntryReservationLifetime);
        var nats = new RecordingNatsClient();
        var logger = new RecordingLogger<GameServer>();
        MatchSessionCleanupService server = CreateLifecycleService(nats, logger, redis);

        server.Publish(
            MatchingLifecycleSubjects.PlayerLeft,
            playerId,
            endedMatchingId);
        await WaitForPendingMatchingRedisCleanupsAsync(server);

        Assert.Equal(
            newerMatchingId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            redis.GetString(MatchingRedisKeys.ReservationKey(playerId)));
        Assert.Equal(1, nats.PublishCount);
        Assert.True(logger.Contains(LogLevel.Warning, "Matching reservation was absent or changed"));
    }

    [Fact]
    public async Task LifecycleReservationReleaseFailure_StillPublishesNats()
    {
        const long playerId = 1203;
        const long matchingId = 42_107;
        var redis = new InMemoryRedisOperations
        {
            StringError = new InvalidOperationException("redis failure")
        };
        var nats = new RecordingNatsClient();
        var logger = new RecordingLogger<GameServer>();
        MatchSessionCleanupService server = CreateLifecycleService(nats, logger, redis);

        server.Publish(
            MatchingLifecycleSubjects.PlayerReleased,
            playerId,
            matchingId);
        await WaitForPendingMatchingRedisCleanupsAsync(server);

        Assert.Equal(1, nats.PublishCount);
        Assert.Equal(MatchingLifecycleSubjects.PlayerReleased, nats.LastSubject);
        Assert.True(logger.Contains(LogLevel.Warning, "Matching reservation release failed before lifecycle publish:"));
    }

    [Fact]
    public async Task Lifecycle_ImmediateWrapperPublishesOnce()
    {
        var nats = new RecordingNatsClient();
        MatchSessionCleanupService server = CreateLifecycleService(
            nats,
            new RecordingLogger<GameServer>());

        server.Publish(
            MatchingLifecycleSubjects.PlayerLeft,
            103L,
            42_005L);
        await WaitForPendingMatchingRedisCleanupsAsync(server);

        Assert.Equal(1, nats.PublishCount);
        Assert.Equal(MatchingLifecycleSubjects.PlayerLeft, nats.LastSubject);
    }

    [Fact]
    public async Task Lifecycle_SecondTerminalSubjectForSamePlayerIsIgnored()
    {
        // left+completed처럼 서로 다른 종료 원인이 충돌하지 않도록 플레이어당 terminal 하나만 선점한다.
        var nats = new RecordingNatsClient();
        MatchSessionCleanupService server = CreateLifecycleService(
            nats,
            new RecordingLogger<GameServer>());
        const long matchingId = 42_006;

        Action? completed = server.PrepareNotification(
            MatchingLifecycleSubjects.PlayerCompleted,
            104L,
            matchingId);
        Assert.NotNull(completed);
        Assert.Null(server.PrepareNotification(
            MatchingLifecycleSubjects.PlayerLeft,
            104L,
            matchingId));
        server.Publish(
            MatchingLifecycleSubjects.PlayerReleased,
            104L,
            matchingId);
        Assert.Equal(0, nats.PublishCount);

        completed!();
        await WaitForPendingMatchingRedisCleanupsAsync(server);

        Assert.Equal(1, nats.PublishCount);
        Assert.Equal(MatchingLifecycleSubjects.PlayerCompleted, nats.LastSubject);

        server.Publish(
            MatchingLifecycleSubjects.PlayerLeft,
            105L,
            matchingId);
        await WaitForPendingMatchingRedisCleanupsAsync(server);
        Assert.Equal(2, nats.PublishCount);
    }

    [Fact]
    public async Task RedisCleanup_DrainWaitsForStartedWorkToFinish()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var redis = new InMemoryRedisOperations { BeforeKeyDeleteAsync = _ => release.Task };
        var service = new MatchSessionCleanupService(redis, new RecordingNatsClient(), new RecordingLogger<GameServer>());
        await redis.StringSetAsync(network.common.MatchingRedisKeys.Key(12345), "pending");
        service.StartMatchDataCleanup(12345);
        Task drain = service.DrainAsync();
        try
        {
            Assert.False(drain.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
        }
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.DrainAsync().IsCompletedSuccessfully);
        Assert.Null(redis.GetString(network.common.MatchingRedisKeys.Key(12345)));
    }

    [Fact]
    public async Task Lifecycle_CloseClosesOwnedNatsClientOnlyOnce()
    {
        var nats = new RecordingNatsClient();
        var service = CreateLifecycleService(nats, new RecordingLogger<GameServer>());

        await service.CloseAsync();
        await service.CloseAsync();

        Assert.Equal(1, nats.CloseCount);
    }

    private MatchSessionCleanupService CreateLifecycleService(
        INatsClient nats,
        ILogger<GameServer> logger,
        IRedisOperations? redisOperations = null)
    {
        var service = new MatchSessionCleanupService(redisOperations ?? new InMemoryRedisOperations(), nats, logger);
        return service;
    }

    private static Task WaitForPendingMatchingRedisCleanupsAsync(MatchSessionCleanupService service) =>
        service.DrainAsync();

    private sealed class RecordingNatsClient : INatsClient
    {
        public int PublishCount { get; private set; }
        public int CloseCount { get; private set; }
        public string? LastSubject { get; private set; }
        public byte[]? LastPayload { get; private set; }
        public Exception? PublishException { get; set; }

        public void Publish(string subject, byte[] message)
        {
            PublishCount++;
            LastSubject = subject;
            LastPayload = message.ToArray();
            if (PublishException != null)
                throw PublishException;
        }

        public void Subscribe(
            string subject,
            Action<string, byte[]> messageHandler,
            string? queue = null) =>
            throw new NotSupportedException();

        public Task<byte[]> RequestAsync(
            string subject,
            byte[] message,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void SubscribeRequest(
            string subject,
            Func<string, byte[], CancellationToken, Task<byte[]?>> messageHandler,
            string? queue = null) =>
            throw new NotSupportedException();

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            CloseCount++;
            return Task.CompletedTask;
        }

        public void Close()
        {
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));

        public bool Contains(LogLevel level, string text) =>
            _entries.Any(entry =>
                entry.Level == level &&
                entry.Message.Contains(text, StringComparison.Ordinal));
    }
}
