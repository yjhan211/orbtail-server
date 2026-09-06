using user_server.matching.queue;
using MessagePack;
using network.common;
using user_server.matching;

namespace demo_regression_tests;

/// <summary>
///     #323 MatchingQueue: 요청 시각 정렬, sanitize의 손상·무효·중복 entry 제거, cutoff 읽기, player별 제거, lock key.
/// </summary>
public sealed class MatchingQueueTests
{
    private readonly InMemoryRedisOperations _cache = new();
    private readonly RecordingLogger _logger = new();
    private readonly MatchingQueue _queue;

    public MatchingQueueTests()
    {
        var reservations = new MatchingReservationCoordinator(_cache, new InMemoryMatchingReservationStore(_cache), _logger);
        _queue = new MatchingQueue(_cache, new FakeRedLockFactory(), reservations, _logger);
    }

    [Fact]
    public void SortByRequestTime_OrdersByRequestTimeThenPlayerId()
    {
        var baseTime = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        MatchingQueueEntry[] entries =
        {
            UserServerMatchingTestData.HumanEntry(30, baseTime.AddSeconds(2)),
            UserServerMatchingTestData.HumanEntry(20, baseTime.AddSeconds(1)),
            UserServerMatchingTestData.HumanEntry(10, baseTime.AddSeconds(2)),
            UserServerMatchingTestData.HumanEntry(40, baseTime)
        };

        MatchingQueueEntry[] sorted = MatchingQueue.SortByRequestTime(entries);

        Assert.Equal(new long[] { 40, 20, 10, 30 }, sorted.Select(entry => entry.PlayerId));
    }

    [Fact]
    public async Task CleanUpEntriesAsync_RemovesMalformedInvalidAndDuplicateEntriesFromQueue()
    {
        MatchingQueueEntry valid = UserServerMatchingTestData.HumanEntry(1);
        MatchingQueueEntry duplicate = UserServerMatchingTestData.HumanEntry(1, requestId: "dup");
        MatchingQueueEntry zeroId = UserServerMatchingTestData.HumanEntry(0);
        MatchingQueueEntry botId = UserServerMatchingTestData.HumanEntry(-5);
        byte[] malformed = { 0xC1, 0xFF, 0x00 };
        byte[][] rawEntries = { valid.Raw, duplicate.Raw, zeroId.Raw, botId.Raw, malformed };
        foreach (byte[] raw in rawEntries)
            await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, raw, 1);

        MatchingQueueEntry[] result = await _queue.CleanUpEntriesAsync(rawEntries);

        Assert.Single(result);
        Assert.Equal(1, result[0].PlayerId);
        Assert.Equal("req1", result[0].RequestId);
        Assert.True(_cache.SortedSetContains(MatchingQueue.QueueKey, valid.Raw));
        Assert.False(_cache.SortedSetContains(MatchingQueue.QueueKey, duplicate.Raw));
        Assert.False(_cache.SortedSetContains(MatchingQueue.QueueKey, zeroId.Raw));
        Assert.False(_cache.SortedSetContains(MatchingQueue.QueueKey, botId.Raw));
        Assert.False(_cache.SortedSetContains(MatchingQueue.QueueKey, malformed));
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task ReadWaitingEntriesAsync_ReturnsOnlyEntriesAtOrBelowCutoff()
    {
        MatchingQueueEntry old = UserServerMatchingTestData.HumanEntry(1);
        MatchingQueueEntry exact = UserServerMatchingTestData.HumanEntry(2);
        MatchingQueueEntry fresh = UserServerMatchingTestData.HumanEntry(3);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, old.Raw, 90);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, exact.Raw, 100);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, fresh.Raw, 101);

        MatchingQueueEntry[] waiting = await _queue.ReadWaitingEntriesAsync(100);

        Assert.Equal(new long[] { 1, 2 }, waiting.Select(entry => entry.PlayerId));
        Assert.Equal(3, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task ReadWaitingEntriesAsync_EmptyQueueReturnsEmpty()
    {
        MatchingQueueEntry[] waiting = await _queue.ReadWaitingEntriesAsync(long.MaxValue);

        Assert.Empty(waiting);
    }

    [Fact]
    public async Task RemovePlayerEntriesAsync_RemovesOnlyThatPlayerAndMalformedEntries()
    {
        MatchingQueueEntry mine = UserServerMatchingTestData.HumanEntry(7);
        MatchingQueueEntry mineStale = UserServerMatchingTestData.HumanEntry(7, requestId: "stale");
        MatchingQueueEntry other = UserServerMatchingTestData.HumanEntry(8);
        byte[] malformed = { 0xC1 };
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, mine.Raw, 1);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, mineStale.Raw, 2);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, other.Raw, 3);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, malformed, 4);

        int removed = await _queue.RemovePlayerEntriesAsync(7);

        Assert.Equal(3, removed);
        Assert.True(_cache.SortedSetContains(MatchingQueue.QueueKey, other.Raw));
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.True(_logger.Contains(Microsoft.Extensions.Logging.LogLevel.Error, "Invalid matching entry removed"));
    }

    [Fact]
    public void MakeLockKey_UsesPlayerScopedPrefix()
    {
        Assert.Equal("matching_queue_lock:42", MatchingQueue.MakeLockKey(42));
    }

    [Fact]
    public void AtomicReservationKeys_ShareRedisClusterHashTag()
    {
        Assert.Equal("{matching}:queue", MatchingQueue.QueueKey);
        Assert.Equal("{matching}:reservation:42", MatchingHandoffRedisKeys.ReservationKey(42));
    }

    [Fact]
    public void MatchingQueueData_KeepsWireKeyNumbersAndTolerateLegacyChannelSlot()
    {
        // 구버전 entry: Key 2(UserChannel)가 채워진 배열도 그대로 읽혀야 한다.
        byte[] legacy = MessagePackSerializer.Serialize(new object?[]
        {
            11L, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), "player.11", null, null, null, null, "legacyreq"
        });

        MatchingQueueEntry entry = MatchingQueueEntry.Parse(legacy);

        Assert.Equal(11, entry.PlayerId);
        Assert.Equal("legacyreq", entry.RequestId);
        Assert.True(entry.IsHuman);
    }
}
