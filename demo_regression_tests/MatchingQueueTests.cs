using user_server.matching.queue;
using MessagePack;
using network.common;
using user_server.matching;

namespace demo_regression_tests;

/// <summary>
///     #323 MatchingQueue: 요청 시각 정렬, sanitize의 손상·무효·중복 request 제거, cutoff 읽기, player별 제거, lock key.
/// </summary>
public sealed class MatchingQueueTests
{
    private readonly InMemoryRedisOperations _cache = new();
    private readonly RecordingLogger _logger = new();
    private readonly MatchingQueue _queue;

    public MatchingQueueTests()
    {
        var reservations = new MatchingReservationService(_cache, _logger);
        _queue = new MatchingQueue(_cache, new FakeRedLockFactory(), reservations, _logger);
    }

    [Fact]
    public void SortByRequestTime_OrdersByRequestTimeThenPlayerId()
    {
        var baseTime = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        MatchingQueueData[] requests =
        {
            UserServerMatchingTestData.HumanEntry(30, baseTime.AddSeconds(2)),
            UserServerMatchingTestData.HumanEntry(20, baseTime.AddSeconds(1)),
            UserServerMatchingTestData.HumanEntry(10, baseTime.AddSeconds(2)),
            UserServerMatchingTestData.HumanEntry(40, baseTime)
        };

        MatchingQueueData[] sorted = MatchingQueue.SortByRequestTime(requests);

        Assert.Equal(new long[] { 40, 20, 10, 30 }, sorted.Select(request => request.PlayerId));
    }

    [Fact]
    public async Task CleanUpRequestsAsync_RemovesMalformedInvalidAndDuplicateEntriesFromQueue()
    {
        MatchingQueueData valid = UserServerMatchingTestData.HumanEntry(1);
        MatchingQueueData duplicate = UserServerMatchingTestData.HumanEntry(1, requestId: "dup");
        MatchingQueueData zeroId = UserServerMatchingTestData.HumanEntry(0);
        MatchingQueueData botId = UserServerMatchingTestData.HumanEntry(-5);
        byte[] malformed = { 0xC1, 0xFF, 0x00 };
        byte[][] rawRequestIds = { UserServerMatchingTestData.RequestBytes(valid), UserServerMatchingTestData.RequestBytes(duplicate), UserServerMatchingTestData.RequestBytes(zeroId), UserServerMatchingTestData.RequestBytes(botId), malformed };
        foreach (var request in new[] { valid, duplicate, zeroId, botId })
            await UserServerMatchingTestData.AddEntryAsync(_cache, request, 1);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, malformed, 1);

        MatchingQueueData[] result = await _queue.CleanUpRequestsAsync(rawRequestIds);

        Assert.Single(result);
        Assert.Equal(1, result[0].PlayerId);
        Assert.Equal("req1", result[0].RequestId);
        Assert.True(_cache.SortedSetContains(MatchingQueue.QueueKey, UserServerMatchingTestData.RequestBytes(valid)));
        Assert.False(_cache.SortedSetContains(MatchingQueue.QueueKey, UserServerMatchingTestData.RequestBytes(duplicate)));
        Assert.False(_cache.SortedSetContains(MatchingQueue.QueueKey, UserServerMatchingTestData.RequestBytes(zeroId)));
        Assert.False(_cache.SortedSetContains(MatchingQueue.QueueKey, UserServerMatchingTestData.RequestBytes(botId)));
        Assert.False(_cache.SortedSetContains(MatchingQueue.QueueKey, malformed));
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task ReadWaitingRequestsAsync_ReturnsOnlyEntriesAtOrBelowCutoff()
    {
        MatchingQueueData old = UserServerMatchingTestData.HumanEntry(1);
        MatchingQueueData exact = UserServerMatchingTestData.HumanEntry(2);
        MatchingQueueData fresh = UserServerMatchingTestData.HumanEntry(3);
        await UserServerMatchingTestData.AddEntryAsync(_cache, old, 90);
        await UserServerMatchingTestData.AddEntryAsync(_cache, exact, 100);
        await UserServerMatchingTestData.AddEntryAsync(_cache, fresh, 101);

        MatchingQueueData[] waiting = await _queue.ReadWaitingRequestsAsync(100);

        Assert.Equal(new long[] { 1, 2 }, waiting.Select(request => request.PlayerId));
        Assert.Equal(3, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task ReadWaitingRequestsAsync_EmptyQueueReturnsEmpty()
    {
        MatchingQueueData[] waiting = await _queue.ReadWaitingRequestsAsync(long.MaxValue);

        Assert.Empty(waiting);
    }

    [Fact]
    public async Task RemovePlayerRequestsAsync_RemovesOnlyThatPlayerAndMalformedEntries()
    {
        MatchingQueueData mine = UserServerMatchingTestData.HumanEntry(7);
        MatchingQueueData mineStale = UserServerMatchingTestData.HumanEntry(7, requestId: "stale");
        MatchingQueueData other = UserServerMatchingTestData.HumanEntry(8);
        byte[] malformed = { 0xC1 };
        await UserServerMatchingTestData.AddEntryAsync(_cache, mine, 1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, mineStale, 2);
        await UserServerMatchingTestData.AddEntryAsync(_cache, other, 3);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, malformed, 4);

        int removed = await _queue.RemovePlayerRequestsAsync(7);

        Assert.Equal(3, removed);
        Assert.True(_cache.SortedSetContains(MatchingQueue.QueueKey, UserServerMatchingTestData.RequestBytes(other)));
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.True(_logger.Contains(Microsoft.Extensions.Logging.LogLevel.Warning, "Invalid matching request removed"));
    }

    [Fact]
    public void MakeLockKey_UsesPlayerScopedPrefix()
    {
        Assert.Equal("matching_queue_lock:42", MatchingQueue.MakeLockKey(42));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("null")]
    [InlineData("mismatched")]
    public async Task ReadWaitingRequestsAsync_RemovesInvalidDetailsAndQueueMember(string kind)
    {
        var request = UserServerMatchingTestData.HumanEntry(42);
        await UserServerMatchingTestData.AddEntryAsync(_cache, request, 1);
        if (kind == "missing")
            await _cache.HashDeleteAsync(MatchingQueue.RequestsKey, request.RequestId);
        else
            await _cache.HashSetAsync(MatchingQueue.RequestsKey, request.RequestId, kind switch
            {
                "malformed" => new byte[] { 0xC1 },
                "null" => new byte[] { 0xC0 },
                _ => MessagePackSerializer.Serialize(UserServerMatchingTestData.HumanEntry(42, requestId: "other"))
            });

        Assert.Empty(await _queue.ReadWaitingRequestsAsync(long.MaxValue));
        Assert.Equal(0, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.Null(_cache.GetHash(MatchingQueue.RequestsKey, request.RequestId));
    }

    [Fact]
    public async Task ReadWaitingRequestsAsync_RedisFailureDoesNotDeleteRequests()
    {
        var request = UserServerMatchingTestData.HumanEntry(42);
        await UserServerMatchingTestData.AddEntryAsync(_cache, request, 1);
        _cache.HashGetError = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => _queue.ReadWaitingRequestsAsync(long.MaxValue));
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.NotNull(_cache.GetHash(MatchingQueue.RequestsKey, request.RequestId));
    }

    [Fact]
    public async Task RemoveRequestAsync_RemovesDetailsButPreservesNewRequest()
    {
        var old = UserServerMatchingTestData.HumanEntry(42, requestId: "old");
        var current = UserServerMatchingTestData.HumanEntry(42, requestId: "current");
        await UserServerMatchingTestData.AddEntryAsync(_cache, old, 1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, current, 2);
        Assert.True(await _queue.RemoveRequestAsync(old));
        Assert.False(await _queue.RemoveRequestAsync(old));
        Assert.Null(_cache.GetHash(MatchingQueue.RequestsKey, old.RequestId));
        Assert.NotNull(_cache.GetHash(MatchingQueue.RequestsKey, current.RequestId));
        Assert.True(_cache.SortedSetContains(MatchingQueue.QueueKey, UserServerMatchingTestData.RequestBytes(current)));
    }

    [Fact]
    public void AtomicReservationKeys_ShareRedisClusterHashTag()
    {
        Assert.Equal("{matching}:queue", MatchingQueue.QueueKey);
        Assert.Equal("{matching}:requests", MatchingQueue.RequestsKey);
        Assert.Equal("{matching}:reservation:42", MatchingRedisKeys.ReservationKey(42));
    }

    [Fact]
    public void MatchingQueueData_UsesStringKeysAndRoundTrips()
    {
        var requestTime = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        byte[] raw = MessagePackSerializer.Serialize(new MatchingQueueData
        {
            PlayerId = 11,
            RequestTime = requestTime,
            RequestId = "request11"
        });

        var reader = new MessagePackReader(raw);
        Assert.Equal(3, reader.ReadMapHeader());
        var keys = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            keys.Add(reader.ReadString()!);
            reader.Skip();
        }
        Assert.Equal(new[] { "playerId", "requestTime", "requestId" }, keys);
        MatchingQueueData request = MatchingQueueData.Parse(raw);

        Assert.Equal(11, request.PlayerId);
        Assert.Equal("request11", request.RequestId);
        Assert.Equal(requestTime, request.RequestTime);
    }
}
