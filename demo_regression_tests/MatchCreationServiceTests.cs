using user_server.matching.creation;
using user_server.matching.queue;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using user_server.matching;

namespace demo_regression_tests;

/// <summary>
///     #323 MatchCreationService: 8인 그룹 단일 매치, 3인+봇 5 채움, reservation 경합 skip, 전달 실패 롤백,
///     전원 success 전달 뒤 admission_ready, 1초 pass의 cutoff·정원 규칙.
/// </summary>
public sealed class MatchCreationServiceTests
{
    private readonly InMemoryRedisOperations _cache = new();
    private readonly MatchingReservationService _reservations;
    private readonly MatchingQueue _queue;
    private readonly RecordingHandoffPublisher _handoff = new();
    private readonly FixedGameServerAllocator _gameServers = new();
    private readonly RecordingLogger _logger = new();

    public MatchCreationServiceTests()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        _reservations = new MatchingReservationService(_cache, _logger);
        _queue = new MatchingQueue(_cache, new FakeRedLockFactory(), _reservations, _logger);
    }

    private MatchCreationService CreatePass(DevMatchOverrides? overrides = null, CancellationToken shutdown = default)
    {
        overrides ??= new DevMatchOverrides(false, false, _cache, new FakeRedLockFactory(), _logger);
        return new MatchCreationService(_cache, _queue, _reservations, _handoff, _gameServers, overrides, shutdown, _logger);
    }

    private async Task<MatchingQueueData[]> EnqueueHumansAsync(int count, double score = 1)
    {
        var entries = new MatchingQueueData[count];
        for (int i = 0; i < count; i++)
        {
            entries[i] = UserServerMatchingTestData.HumanEntry(1_000 + i);
            await UserServerMatchingTestData.AddEntryAsync(_cache, entries[i], score + i);
        }

        return entries;
    }

    private string? ReservationOf(long playerId) => _cache.GetString(MatchingRedisKeys.ReservationKey(playerId));

    [Fact]
    public async Task CreateMatchAsync_WithoutGameServerNodeLeavesQueueAndReservationsUntouched()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(8);
        _gameServers.Allocation = null;

        bool committed = await CreatePass().CreateMatchAsync(humans, 0, MatchCreationOrigin.Queue);

        Assert.False(committed);
        Assert.Equal(1, _gameServers.Calls);
        Assert.Equal(8, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.Null(_cache.GetString(MatchCreationService.MatchingIdKey));
        Assert.All(humans, human => Assert.Null(ReservationOf(human.PlayerId)));
        Assert.Empty(_handoff.Events);
    }

    [Fact]
    public async Task CreateMatchAsync_EightHumansMakeOneMatchWithoutBots()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(8);

        bool committed = await CreatePass().CreateMatchAsync(humans, 0, MatchCreationOrigin.Queue);

        Assert.True(committed);
        Assert.Equal("1", _cache.GetString(MatchCreationService.MatchingIdKey));
        Assert.Equal(0, _handoff.StoredManifests[1].BotCount);
        Assert.Equal(humans.Select(h => h.PlayerId).OrderBy(id => id), _handoff.StoredManifests[1].HumanPlayerIds.OrderBy(id => id));
        Assert.Equal(8, _handoff.Deliveries.Count);
        Assert.All(_handoff.DeliveredNodeIds, nodeId => Assert.Equal(FixedGameServerAllocator.DefaultNodeId, nodeId));
        Assert.Equal(humans.Select(h => h.PlayerId).OrderBy(id => id), _handoff.Deliveries.Select(d => d.Entry.PlayerId).OrderBy(id => id));
        Assert.All(humans, human => Assert.Equal("1", ReservationOf(human.PlayerId)));
        Assert.Equal(0, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.Empty(await _cache.HashGetAllAsync(MatchingQueue.RequestsKey));
        Assert.Contains("ready:1", _handoff.Events);
        Assert.Contains($"watchdog:1:{string.Join(",", humans.Select(h => h.PlayerId))}", _handoff.Events);
        Assert.DoesNotContain(_handoff.Events, e => e.StartsWith("cancel:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateMatchAsync_ThreeHumansAreFilledWithFiveBots()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(3);

        bool committed = await CreatePass().CreateMatchAsync(humans, 5, MatchCreationOrigin.Queue);

        Assert.True(committed);
        network.common.data.models.MatchManifest manifest = _handoff.StoredManifests[1];
        Assert.Equal(5, manifest.BotCount);
        Assert.Equal(3, manifest.HumanPlayerIds.Count);
        Assert.Equal(3, _handoff.Deliveries.Count);
        Assert.Equal("1", ReservationOf(1_000));
        Assert.Contains("ready:1", _handoff.Events);
    }

    [Fact]
    public async Task CreateMatchAsync_SuccessPacketsReachEveryHumanBeforeAdmissionReady()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(4);

        await CreatePass().CreateMatchAsync(humans, 4, MatchCreationOrigin.Queue);

        int botsIndex = _handoff.Events.IndexOf("manifest:1:4+4");
        int readyIndex = _handoff.Events.IndexOf("ready:1");
        int watchdogIndex = _handoff.Events.FindIndex(e => e.StartsWith("watchdog:1:", StringComparison.Ordinal));
        int[] deliverIndexes = _handoff.Events
            .Select((e, index) => (e, index))
            .Where(pair => pair.e.StartsWith("deliver:1:", StringComparison.Ordinal))
            .Select(pair => pair.index)
            .ToArray();
        Assert.Equal(4, deliverIndexes.Length);
        Assert.True(botsIndex >= 0 && botsIndex < deliverIndexes.Min());
        Assert.True(deliverIndexes.Max() < readyIndex);
        Assert.True(readyIndex < watchdogIndex);
    }

    [Fact]
    public async Task CreateMatchAsync_RejectedDeliveryRollsBackReservationsHandoffAndNotifiesBatch()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(3);
        _handoff.DeliverResult = playerId => playerId != 1_001;

        bool committed = await CreatePass().CreateMatchAsync(humans, 5, MatchCreationOrigin.Queue);

        Assert.False(committed);
        Assert.DoesNotContain("ready:1", _handoff.Events);
        Assert.Contains("cancel:1", _handoff.Events);
        Assert.Contains("delete:1", _handoff.Events);
        Assert.Contains("notify_failed:1:1000,1001,1002", _handoff.Events);
        Assert.All(humans, human => Assert.Null(ReservationOf(human.PlayerId)));
        Assert.Equal(0, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.True(_logger.Contains(LogLevel.Warning, "Matching rolled back because delivery was incomplete"));
    }

    [Fact]
    public async Task CreateMatchAsync_DeliveryExceptionIsContainedAndRolledBack()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(2);
        _handoff.ThrowOnDeliver.Add(1_000);

        bool committed = await CreatePass().CreateMatchAsync(humans, 6, MatchCreationOrigin.BotFill);

        Assert.False(committed);
        Assert.Equal(2, _handoff.Deliveries.Count);
        Assert.Contains("cancel:1", _handoff.Events);
        Assert.All(humans, human => Assert.Null(ReservationOf(human.PlayerId)));
        Assert.True(_logger.Contains(LogLevel.Error, "Failed to commit matching request"));
    }

    [Fact]
    public async Task CreateMatchAsync_CompletedAdmissionIsNotRolledBack()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(2);
        _handoff.DeliverResult = playerId => playerId != 1_001;
        _handoff.CancelAdmissionResult = false;

        bool committed = await CreatePass().CreateMatchAsync(humans, 6, MatchCreationOrigin.Queue);

        Assert.False(committed);
        Assert.Contains("cancel:1", _handoff.Events);
        Assert.DoesNotContain("delete:1", _handoff.Events);
        Assert.DoesNotContain(_handoff.Events, e => e.StartsWith("notify_failed:", StringComparison.Ordinal));
        // 전달된 사람의 active reservation은 그대로 남고(TTL fallback), 거절된 사람의 reservation만 즉시 해제된다.
        Assert.Equal("1", ReservationOf(1_000));
        Assert.Null(ReservationOf(1_001));
    }

    [Fact]
    public async Task CreateMatchAsync_ReservationContentionSkipsGroupWithoutIssuingMatchingId()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(2);
        await _cache.StringSetAsync(MatchingRedisKeys.ReservationKey(1_001), "other-worker");

        bool committed = await CreatePass().CreateMatchAsync(humans, 6, MatchCreationOrigin.Queue);

        Assert.False(committed);
        Assert.Null(_cache.GetString(MatchCreationService.MatchingIdKey));
        Assert.Empty(_handoff.Events);
        Assert.Null(ReservationOf(1_000));
        Assert.Equal("other-worker", ReservationOf(1_001));
        Assert.Equal(2, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task CreateMatchAsync_WatchdogRefusalDuringShutdownRollsBackAfterReady()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(1);
        _handoff.WatchdogResult = false;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreatePass().CreateMatchAsync(humans, 7, MatchCreationOrigin.Queue));

        Assert.Contains("ready:1", _handoff.Events);
        Assert.Contains("cancel:1", _handoff.Events);
        Assert.Null(ReservationOf(1_000));
    }

    [Fact]
    public async Task RunAsync_DefaultRulesMatchEachWaitingHumanSeparatelyWithSevenBots()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueData waitedA = UserServerMatchingTestData.HumanEntry(1);
        MatchingQueueData waitedB = UserServerMatchingTestData.HumanEntry(2);
        MatchingQueueData fresh = UserServerMatchingTestData.HumanEntry(3);
        await UserServerMatchingTestData.AddEntryAsync(_cache, waitedA, now - 10);
        await UserServerMatchingTestData.AddEntryAsync(_cache, waitedB, now - 9);
        await UserServerMatchingTestData.AddEntryAsync(_cache, fresh, now);

        await CreatePass().RunAsync();

        Assert.Equal("2", _cache.GetString(MatchCreationService.MatchingIdKey));
        Assert.Equal(2, _handoff.StoredManifests.Count);
        Assert.All(_handoff.StoredManifests.Values, manifest => Assert.Equal(7, manifest.BotCount));
        Assert.Equal(new long[] { 1, 2 }, _handoff.Deliveries.Select(d => d.Entry.PlayerId));
        Assert.Equal("1", ReservationOf(1));
        Assert.Equal("2", ReservationOf(2));
        Assert.Null(ReservationOf(3));
        Assert.True(_cache.SortedSetContains(MatchingQueue.QueueKey, UserServerMatchingTestData.RequestBytes(fresh)));
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task RunAsync_SoloMapValidationPublishesModeWithoutBots()
    {
        var overrides = new DevMatchOverrides(false, true, _cache, new FakeRedLockFactory(), _logger);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueData player = UserServerMatchingTestData.HumanEntry(1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, player, now - 10);

        await CreatePass(overrides).RunAsync();

        MatchManifest manifest = Assert.Single(_handoff.StoredManifests).Value;
        Assert.Equal(MatchMode.SoloMapValidation, manifest.Mode);
        Assert.Equal(new long[] { player.PlayerId }, manifest.HumanPlayerIds);
        Assert.Equal(0, manifest.BotCount);
        Assert.Single(_handoff.Deliveries);
    }

    [Fact]
    public async Task RunAsync_EmptyQueueDoesNothing()
    {
        await CreatePass().RunAsync();

        Assert.Null(_cache.GetString(MatchCreationService.MatchingIdKey));
        Assert.Empty(_handoff.Events);
    }

    [Fact]
    public async Task RunAsync_ShutdownTokenStopsBeforeAnyGroup()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueData waited = UserServerMatchingTestData.HumanEntry(1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, waited, now - 10);
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();

        await CreatePass(shutdown: shutdown.Token).RunAsync();

        Assert.Empty(_handoff.Events);
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task RunAsync_TwoPlayerTestWaitsForSecondHuman()
    {
        var overrides = new DevMatchOverrides(true, false, _cache, new FakeRedLockFactory(), _logger);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueData alone = UserServerMatchingTestData.HumanEntry(1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, alone, now - 100);

        await CreatePass(overrides).RunAsync();

        Assert.Empty(_handoff.Events);
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }
}
