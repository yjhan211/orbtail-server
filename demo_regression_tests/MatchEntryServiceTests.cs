using network.common;
using network.common.data.models;
using network.gamehandoff;
using user_server.matching;
using user_server.matching.queue;
using user_server.sessions;

namespace demo_regression_tests;

public sealed class MatchEntryServiceTests
{
    [Fact]
    public async Task ReadyMarker_SuccessWritesOnceWithoutReadBack()
    {
        var redis = new InMemoryRedisOperations();
        await CreateService(redis).MarkEntryReadyAsync(42);

        Assert.Equal(1, redis.HashSetWithExpiryCalls);
        Assert.Equal(0, redis.HashGetCalls);
        Assert.Equal(new byte[] { MatchingRedisKeys.EntryReadyValue },
            redis.GetHash(MatchingRedisKeys.Key(42), MatchingRedisKeys.EntryReadyField));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReadyMarker_WriteErrorPropagatesWithoutReadBackOrRetry(bool writeApplied, bool readFails)
    {
        var writeError = new TimeoutException("Write response missing");
        var redis = new InMemoryRedisOperations
        {
            HashSetWithExpiryError = writeError,
            ApplyHashSetBeforeError = writeApplied,
            HashGetError = readFails ? new TimeoutException("Read failed") : null
        };
        var service = CreateService(redis);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => service.MarkEntryReadyAsync(42));
        Assert.Same(writeError, error);

        Assert.Equal(1, redis.HashSetWithExpiryCalls);
        Assert.Equal(0, redis.HashGetCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PendingState_WriteErrorPropagatesWithoutReadBackOrRetry(bool writeApplied, bool readFails)
    {
        var writeError = new TimeoutException("State write response missing");
        var redis = new InMemoryRedisOperations
        {
            StringError = writeApplied ? null : writeError,
            StringSetIfNotExistsResponseError = writeApplied ? writeError : null,
            StringGetError = readFails ? new TimeoutException("State read failed") : null
        };
        var error = await Assert.ThrowsAsync<TimeoutException>(() => CreateService(redis).MarkEntryReadyAsync(42));
        Assert.Same(writeError, error);
        Assert.Equal(0, redis.HashSetWithExpiryCalls);
        Assert.Equal(1, redis.StringSetIfNotExistsCalls);
        Assert.Equal(0, redis.StringGetCalls);
    }

    [Theory]
    [InlineData("pending", true)]
    [InlineData("completed", false)]
    [InlineData("canceled", false)]
    public async Task PendingState_ExistingStateIsNeverOverwritten(string state, bool accepted)
    {
        var redis = new InMemoryRedisOperations();
        await redis.StringSetAsync(MatchingRedisKeys.EntryStateKey(42), state);
        if (accepted)
            await CreateService(redis).MarkEntryReadyAsync(42);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(redis).MarkEntryReadyAsync(42));

        Assert.Equal(state, redis.GetString(MatchingRedisKeys.EntryStateKey(42)));
        Assert.Equal(1, redis.StringSetIfNotExistsCalls);
        Assert.Equal(1, redis.StringGetCalls);
        Assert.Equal(accepted ? 1 : 0, redis.HashSetWithExpiryCalls);
    }

    [Theory]
    [InlineData(null, true, 1)]
    [InlineData("pending", true, 0)]
    [InlineData("canceled", true, 1)]
    [InlineData("completed", false, 1)]
    [InlineData("unknown", false, 1)]
    public async Task CancelEntry_AttemptsOnceAndPreservesCompletedState(string? state, bool expected, int reads)
    {
        var redis = new InMemoryRedisOperations();
        string key = MatchingRedisKeys.EntryStateKey(42);
        if (state != null)
            await redis.StringSetAsync(key, state);

        Assert.Equal(expected, await CreateService(redis).TryCancelEntryForRollbackAsync(42));
        Assert.Equal(1, redis.StringSetIfEqualsCalls);
        Assert.Equal(reads, redis.StringGetCalls);
        Assert.Equal(state == "pending" ? "canceled" : state, redis.GetString(key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelEntry_RedisErrorReturnsFalseWithoutRetry(bool failRead)
    {
        var error = new TimeoutException("Redis unavailable");
        var redis = new InMemoryRedisOperations
        {
            StringError = failRead ? null : error,
            StringGetError = failRead ? error : null
        };
        Assert.False(await CreateService(redis).TryCancelEntryForRollbackAsync(42));
        Assert.Equal(1, redis.StringSetIfEqualsCalls);
        Assert.Equal(failRead ? 1 : 0, redis.StringGetCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CancelEntry_InvalidMatchIdDoesNotAccessRedis(long matchingId)
    {
        var redis = new InMemoryRedisOperations();
        Assert.True(await CreateService(redis).TryCancelEntryForRollbackAsync(matchingId));
        Assert.Equal(0, redis.StringSetIfEqualsCalls);
        Assert.Equal(0, redis.StringGetCalls);
    }

    [Fact]
    public void EntryTimeout_EmptyRosterDoesNotRegisterTask()
    {
        bool registered = false;
        var service = CreateService(new InMemoryRedisOperations(), (_, _) => { registered = true; return true; });
        Assert.False(service.StartEntryTimeoutCheck(42, []));
        Assert.False(registered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntryTimeout_ReturnsRegistrationResultAndStopsOnShutdown(bool accepted)
    {
        Func<Task>? operation = null;
        using var cancellation = new CancellationTokenSource();
        var redis = new InMemoryRedisOperations();
        var service = CreateService(redis, (task, name) =>
        {
            Assert.Equal("matching-entry-timeout:42", name);
            operation = task;
            return accepted;
        }, cancellation.Token);
        Assert.Equal(accepted, service.StartEntryTimeoutCheck(42, [7]));
        Assert.NotNull(operation);
        cancellation.Cancel();
        await operation!();
        Assert.Equal(0, redis.StringSetIfEqualsCalls);
        Assert.Equal(0, redis.StringGetCalls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("rejected")]
    [InlineData("exception")]
    public async Task BatchFailure_ContinuesAfterEachOutcomeAndSkipsDuplicatePlayers(string firstOutcome)
    {
        var delivered = new List<long>();
        var router = new UnusedSessionRouter
        {
            OnMatchingFailed = (playerId, matchingId, requestId, errorCode) =>
            {
                delivered.Add(playerId);
                Assert.Equal(42, matchingId);
                Assert.Equal($"request-{playerId}", requestId);
                Assert.Equal(ErrorCode.MATCHING_FAILED, errorCode);
                if (playerId == 1 && firstOutcome == "exception")
                    return Task.FromException<bool>(new InvalidOperationException("Delivery failed"));
                return Task.FromResult(playerId != 1 || firstOutcome != "rejected");
            }
        };
        var service = CreateService(new InMemoryRedisOperations(), sessionRouter: router);
        await service.NotifyBatchFailedAsync(
        [
            new MatchingQueueData { PlayerId = 1, RequestId = "request-1" },
            new MatchingQueueData { PlayerId = 2, RequestId = "request-2" },
            new MatchingQueueData { PlayerId = 1, RequestId = "duplicate" },
            new MatchingQueueData { PlayerId = 3, RequestId = "request-3" }
        ], 42);
        Assert.Equal(new long[] { 1, 2, 3 }, delivered);
    }

    private static MatchEntryService CreateService(
        InMemoryRedisOperations redis,
        Func<Func<Task>, string, bool>? registerTask = null,
        CancellationToken shutdownToken = default,
        IPlayerSessionRouter? sessionRouter = null)
    {
        var logger = new RecordingLogger();
        var router = sessionRouter ?? new UnusedSessionRouter();
        return new MatchEntryService(
            redis,
            new GameHandoffTicketService(new RedisGameHandoffTicketStore(redis), new GameHandoffTicketOptions()),
            new MatchingReservationService(redis, logger),
            router,
            registerTask ?? ((_, _) => false),
            logger,
            shutdownToken);
    }

    private sealed class UnusedSessionRouter : IPlayerSessionRouter
    {
        public Func<long, long, string, ErrorCode, Task<bool>>? OnMatchingFailed { get; init; }
        public Task<bool> DeliverMatchingSuccessAsync(long playerId, string requestId, U_TO_C_MATCHING_SUCCESS result) =>
            throw new NotSupportedException();
        public Task<bool> DeliverMatchingFailedAsync(long playerId, long matchingId, string requestId, ErrorCode errorCode) =>
            OnMatchingFailed?.Invoke(playerId, matchingId, requestId, errorCode) ?? throw new NotSupportedException();
        public Task<bool> DeliverEntryFailedAsync(long playerId, long matchingId, ErrorCode errorCode) =>
            throw new NotSupportedException();
        public void ClearMatchingAssignment(long playerId, long matchingId) => throw new NotSupportedException();
        public void AnnounceLogin(long playerId, long generation) => throw new NotSupportedException();
    }
}
