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


namespace demo_regression_tests;

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
        await redis.HashSetAsync("matching_bots", 12345, new byte[] { 1 });
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
        Assert.Null(redis.GetHash("matching_bots", 12345));
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

    [Fact]
    public void Lifecycle_SourceContract_ClaimsTerminalBeforeOneShotCorePublish()
    {
        string root = FindRepositoryRoot();
        string serverSource = ReadNormalizedSource(root, "game_server", "Program.cs") + ReadNormalizedSource(root, "game_server", "GameServer.cs") + ReadNormalizedSource(root, "game_server", "Matches", "MatchSessionCleanupService.cs");
        string immediateWrapper = ReadMethodSlice(
            serverSource,
            "internal void Publish(",
            "internal Action? PrepareNotification(");
        string preparation = ReadMethodSlice(
            serverSource,
            "internal Action? PrepareNotification(",
            "private async Task ReleaseReservationAndPublishAsync(");
        string corePublish = ReadMethodSlice(
            serverSource,
            "private async Task ReleaseReservationAndPublishAsync(",
            "internal async Task CloseAsync(");

        int immediatePreparation = Find(
            immediateWrapper,
            "var dispatch = PrepareNotification(subject, playerId, matchingId);");
        int immediateDispatch = Find(immediateWrapper, "dispatch?.Invoke();");
        Assert.True(immediatePreparation < immediateDispatch);
        Assert.DoesNotContain("PublishMatchingLifecycleCore(", immediateWrapper, StringComparison.Ordinal);

        int terminalClaim = Find(
            preparation,
            "if (!playerSubjects.TryAdd(playerId, subject))");
        int claimRejected = preparation.IndexOf("return null;", terminalClaim, StringComparison.Ordinal);
        int deferredFactory = Find(preparation, "return () =>");
        int exactlyOnceGuard = Find(preparation, "Interlocked.Exchange(ref dispatchStarted, 1)");
        int trackedPublication = Find(
            preparation,
            "_ = ReleaseReservationAndPublishAsync(");
        Assert.True(terminalClaim < claimRejected);
        Assert.True(claimRejected < deferredFactory);
        Assert.True(deferredFactory < exactlyOnceGuard);
        Assert.True(exactlyOnceGuard < trackedPublication);

        int serialization = Find(corePublish, "MessagePackSerializer.Serialize(new G_TO_U_MATCHING_LIFECYCLE");
        Assert.Contains("PlayerId = playerId", corePublish);
        Assert.Contains("MatchingId = matchingId", corePublish);
        int corePublishInvocation = Find(corePublish, "_natsClient?.Publish(subject, payload);");
        int failureLog = Find(corePublish, "Matching lifecycle publish failed:");
        Assert.True(serialization < corePublishInvocation);
        Assert.True(corePublishInvocation < failureLog);
        Assert.DoesNotContain("throw", corePublish, StringComparison.Ordinal);

        // #320 단일 노드 — durable outbox·JetStream·owner fence 경로는 GameServer에 남지 않는다.
        Assert.DoesNotContain("Outbox", serverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("JetStream", serverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerFence", serverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishDurableAsync", serverSource, StringComparison.Ordinal);

        string shutdown = ReadMethodSlice(
            serverSource,
            "private async Task StopCoreAsync(",
            "private void InitializeServices(");
        int timerDisposal = Find(shutdown, "await tickService.StopAsync();");
        int redisCleanupDrain = Find(shutdown, "matchSessionCleanup.DrainAsync()");
        int natsClose = Find(shutdown, "matchSessionCleanup.CloseAsync();");
        Assert.True(timerDisposal < redisCleanupDrain);
        Assert.True(redisCleanupDrain < natsClose);
    }

    [Fact]
    public void RedisCleanup_SourceContract_StartsAfterUnlockAndTracksBeforeDispatch()
    {
        string root = FindRepositoryRoot();
        string lifecycle = ReadNormalizedSource(root, "game_server", "Matches", "MatchSessionCleanupService.cs");
        string start = ReadMethodSlice(lifecycle, "internal void StartMatchDataCleanup(", "private async Task DeleteMatchDataAsync(");
        Assert.True(Find(start, "_pendingTasks.TryAdd") < Find(start, "_ = DeleteMatchDataAsync("));
        string worker = ReadMethodSlice(lifecycle, "private async Task DeleteMatchDataAsync(", "private void CompleteCleanupTask(");
        Assert.True(Find(worker, "finally") < Find(worker, "CompleteCleanupTask(operationId, completion);"));
        string runtime = ReadNormalizedSource(root, "game_server", "Matches", "MatchRuntime.cs");
        string exit = runtime.Substring(runtime.IndexOf("internal void Exit()", StringComparison.Ordinal));
        Assert.True(Find(exit, "_runtimeStore.RemoveCompleted(this);") < Find(exit, "Monitor.Exit(MatchLock);"));
        Assert.True(Find(exit, "Monitor.Exit(MatchLock);") < Find(exit, "foreach (var action in afterRelease)"));
        Assert.True(Find(exit, "foreach (var action in afterRelease)") < Find(exit, "_matchSessionCleanup.StartMatchDataCleanup(MatchingId);"));
        Assert.Contains("if (IsEnded && !_cleanupStarted)", exit);
        Assert.Contains("startRedisCleanup = true;", exit);
    }
    private static int Find(string source, string marker)
    {
        int index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Could not find source marker '{marker}'.");
        return index;
    }

    private static string ReadMethodSlice(string source, string startMarker, string endMarker)
    {
        int startIndex = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find method start marker '{startMarker}'.");
        int endIndex = source.IndexOf(
            endMarker,
            startIndex + startMarker.Length,
            StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Could not find method end marker '{endMarker}'.");
        return source[startIndex..endIndex];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private static string ReadNormalizedSource(string repositoryRoot, params string[] pathParts)
    {
        string[] fullPathParts = [repositoryRoot, .. pathParts];
        return File.ReadAllText(Path.Combine(fullPathParts)).Replace("\r\n", "\n");
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
