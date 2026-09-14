using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using user_server.matching;
using user_server.matching.creation;
using user_server.matching.queue;

namespace demo_regression_tests;

/// <summary>
///     #323 MatchCreationService: 8인 그룹 단일 매치, 3인+봇 5 채움, reservation 경합 skip, 전달 실패 롤백,
///     전원 success 전달 뒤 entry_ready, 1초 pass의 cutoff·정원 규칙.
/// </summary>
public sealed class MatchCreationServiceTests
{
    private readonly InMemoryRedisOperations _cache = new();
    private readonly MatchingReservationService _reservations;
    private readonly MatchingQueue _queue;
    private readonly RecordingEntryPublisher _entryService = new();
    private readonly FixedGameServerAllocator _gameServers = new();
    private readonly RecordingLogger _logger = new();

    public MatchCreationServiceTests()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        _reservations = new MatchingReservationService(_cache, _logger.For<MatchingReservationService>());
        _queue = new MatchingQueue(_cache, new FakeRedLockFactory(), _reservations, _logger.For<MatchingQueue>());
    }

    private MatchCreationService CreatePass(bool soloMapValidation = false, CancellationToken shutdown = default)
    {
        return new MatchCreationService(_cache, _queue, _reservations, _entryService, _gameServers, soloMapValidation, _logger.For<MatchCreationService>(), shutdown);
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

        bool committed = await CreatePass().CreateMatchAsync(humans, 0);

        Assert.False(committed);
        Assert.Equal(1, _gameServers.Calls);
        Assert.Equal(8, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.Null(_cache.GetString(MatchingRedisKeys.MatchingIdKey));
        Assert.All(humans, human => Assert.Null(ReservationOf(human.PlayerId)));
        Assert.Empty(_entryService.Events);
    }

    [Fact]
    public async Task CreateMatchAsync_EightHumansMakeOneMatchWithoutBots()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(8);

        bool committed = await CreatePass().CreateMatchAsync(humans, 0);

        Assert.True(committed);
        Assert.Equal("1", _cache.GetString(MatchingRedisKeys.MatchingIdKey));
        Assert.Equal(0, _entryService.StoredManifests[1].BotCount);
        Assert.Equal(humans.Select(h => h.PlayerId).OrderBy(id => id), _entryService.StoredManifests[1].HumanPlayerIds.OrderBy(id => id));
        Assert.Equal(8, _entryService.Deliveries.Count);
        Assert.All(_entryService.DeliveredNodeIds, nodeId => Assert.Equal(FixedGameServerAllocator.DefaultNodeId, nodeId));
        Assert.Equal(humans.Select(h => h.PlayerId).OrderBy(id => id), _entryService.Deliveries.Select(d => d.Entry.PlayerId).OrderBy(id => id));
        Assert.All(humans, human => Assert.Equal("1", ReservationOf(human.PlayerId)));
        Assert.Equal(0, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.Empty(await _cache.HashGetAllAsync(MatchingQueue.RequestsKey));
        Assert.Contains("ready:1", _entryService.Events);
        Assert.Contains($"watchdog:1:{string.Join(",", humans.Select(h => h.PlayerId))}", _entryService.Events);
        Assert.DoesNotContain(_entryService.Events, e => e.StartsWith("cancel:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateMatchAsync_ThreeHumansAreFilledWithFiveBots()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(3);

        bool committed = await CreatePass().CreateMatchAsync(humans, 5);

        Assert.True(committed);
        network.common.data.models.MatchManifest manifest = _entryService.StoredManifests[1];
        Assert.Equal(5, manifest.BotCount);
        Assert.Equal(3, manifest.HumanPlayerIds.Count);
        Assert.Equal(3, _entryService.Deliveries.Count);
        Assert.Equal("1", ReservationOf(1_000));
        Assert.Contains("ready:1", _entryService.Events);
    }

    [Fact]
    public async Task CreateMatchAsync_SuccessPacketsReachEveryHumanBeforeEntryReady()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(4);

        await CreatePass().CreateMatchAsync(humans, 4);

        int botsIndex = _entryService.Events.IndexOf("manifest:1:4+4");
        int readyIndex = _entryService.Events.IndexOf("ready:1");
        int watchdogIndex = _entryService.Events.FindIndex(e => e.StartsWith("watchdog:1:", StringComparison.Ordinal));
        int[] deliverIndexes = _entryService.Events
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
    public async Task CreateMatchAsync_RejectedDeliveryRollsBackReservationsEntryAndNotifiesBatch()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(3);
        _entryService.DeliverResult = playerId => playerId != 1_001;

        bool committed = await CreatePass().CreateMatchAsync(humans, 5);

        Assert.False(committed);
        Assert.DoesNotContain("ready:1", _entryService.Events);
        Assert.Contains("cancel:1", _entryService.Events);
        Assert.Contains("delete:1", _entryService.Events);
        Assert.Contains("notify_failed:1:1000,1001,1002", _entryService.Events);
        Assert.All(humans, human => Assert.Null(ReservationOf(human.PlayerId)));
        Assert.Equal(0, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.True(_logger.Contains(LogLevel.Warning, "Matching rolled back because delivery was incomplete"));
    }

    [Fact]
    public async Task CreateMatchAsync_DeliveryExceptionIsContainedAndRolledBack()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(2);
        _entryService.ThrowOnDeliver.Add(1_000);

        bool committed = await CreatePass().CreateMatchAsync(humans, 6);

        Assert.False(committed);
        Assert.Equal(2, _entryService.Deliveries.Count);
        Assert.Contains("cancel:1", _entryService.Events);
        Assert.All(humans, human => Assert.Null(ReservationOf(human.PlayerId)));
        Assert.True(_logger.Contains(LogLevel.Error, "Failed to commit matching request"));
    }

    [Fact]
    public async Task CreateMatchAsync_CompletedEntryIsNotRolledBack()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(2);
        _entryService.DeliverResult = playerId => playerId != 1_001;
        _entryService.CancelEntryResult = false;

        bool committed = await CreatePass().CreateMatchAsync(humans, 6);

        Assert.False(committed);
        Assert.Contains("cancel:1", _entryService.Events);
        Assert.DoesNotContain("delete:1", _entryService.Events);
        Assert.DoesNotContain(_entryService.Events, e => e.StartsWith("notify_failed:", StringComparison.Ordinal));
        // 전달된 사람의 active reservation은 그대로 남고(TTL fallback), 거절된 사람의 reservation만 즉시 해제된다.
        Assert.Equal("1", ReservationOf(1_000));
        Assert.Null(ReservationOf(1_001));
    }

    [Fact]
    public async Task CreateMatchAsync_ReservationContentionSkipsGroupAndConsumesMatchingId()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(2);
        await _cache.StringSetAsync(MatchingRedisKeys.ReservationKey(1_001), "other-worker");

        bool committed = await CreatePass().CreateMatchAsync(humans, 6);

        Assert.False(committed);
        Assert.Equal("1", _cache.GetString(MatchingRedisKeys.MatchingIdKey));
        Assert.Empty(_entryService.Events);
        Assert.Null(ReservationOf(1_000));
        Assert.Equal("other-worker", ReservationOf(1_001));
        Assert.Equal(2, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task CreateMatchAsync_WatchdogRefusalDuringShutdownRollsBackAfterReady()
    {
        MatchingQueueData[] humans = await EnqueueHumansAsync(1);
        _entryService.WatchdogResult = false;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreatePass().CreateMatchAsync(humans, 7));

        Assert.Contains("ready:1", _entryService.Events);
        Assert.Contains("cancel:1", _entryService.Events);
        Assert.Null(ReservationOf(1_000));
    }

    [Fact]
    public async Task RunAsync_GroupsWaitingHumansTogetherAndLeavesFreshRequest()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueData waitedA = UserServerMatchingTestData.HumanEntry(1);
        MatchingQueueData waitedB = UserServerMatchingTestData.HumanEntry(2);
        MatchingQueueData fresh = UserServerMatchingTestData.HumanEntry(3);
        await UserServerMatchingTestData.AddEntryAsync(_cache, waitedA, now - 10);
        await UserServerMatchingTestData.AddEntryAsync(_cache, waitedB, now - 9);
        await UserServerMatchingTestData.AddEntryAsync(_cache, fresh, now);

        bool soloMapValidation = false;
        await CreatePass(soloMapValidation).RunAsync();

        Assert.Equal("1", _cache.GetString(MatchingRedisKeys.MatchingIdKey));
        Assert.Single(_entryService.StoredManifests);
        Assert.All(_entryService.StoredManifests.Values, manifest => Assert.Equal(6, manifest.BotCount));
        Assert.Equal(new long[] { 1, 2 }, _entryService.Deliveries.Select(d => d.Entry.PlayerId));
        Assert.Equal("1", ReservationOf(1));
        Assert.Equal("1", ReservationOf(2));
        Assert.Null(ReservationOf(3));
        Assert.True(_cache.SortedSetContains(MatchingQueue.QueueKey, UserServerMatchingTestData.RequestBytes(fresh)));
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task RunAsync_SoloMapValidationPublishesModeWithoutBots()
    {
        bool soloMapValidation = true;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueData player = UserServerMatchingTestData.HumanEntry(1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, player, now - 10);

        await CreatePass(soloMapValidation).RunAsync();

        MatchManifest manifest = Assert.Single(_entryService.StoredManifests).Value;
        Assert.Equal(MatchMode.SoloMapValidation, manifest.Mode);
        Assert.Equal(new long[] { player.PlayerId }, manifest.HumanPlayerIds);
        Assert.Equal(0, manifest.BotCount);
        Assert.Single(_entryService.Deliveries);
    }

    [Fact]
    public async Task RunAsync_EmptyQueueDoesNothing()
    {
        await CreatePass().RunAsync();

        Assert.Null(_cache.GetString(MatchingRedisKeys.MatchingIdKey));
        Assert.Empty(_entryService.Events);
    }

    [Fact]
    public async Task RunAsync_SoloValidationCreatesSeparateBotFreeMatches()
    {
        await EnqueueHumansAsync(2, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10);
        await CreatePass(soloMapValidation: true).RunAsync();

        Assert.Equal(2, _entryService.StoredManifests.Count);
        Assert.All(_entryService.StoredManifests.Values, manifest =>
        {
            Assert.Single(manifest.HumanPlayerIds);
            Assert.Equal(0, manifest.BotCount);
            Assert.Equal(MatchMode.SoloMapValidation, manifest.Mode);
        });
    }

    [Theory]
    [InlineData(1, 0, 0, 1)]
    [InlineData(1, 5, 1, 0)]
    [InlineData(3, 10, 1, 0)]
    [InlineData(7, 20, 1, 0)]
    [InlineData(8, 20, 1, 0)]
    [InlineData(1, 60, 1, 0)]
    [InlineData(7, 60, 1, 0)]
    [InlineData(10, 60, 2, 0)]
    public async Task RunAsync_NormalRulesGroupEligibleRequestsAndFillRemainder(
        int humans, int ageSeconds, int matches, int remaining)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await EnqueueHumansAsync(humans, now - ageSeconds);

        await CreatePass().RunAsync();

        Assert.Equal(matches, _entryService.StoredManifests.Count);
        Assert.Equal(remaining, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.All(_entryService.StoredManifests.Values, manifest =>
            Assert.Equal(8, manifest.HumanPlayerIds.Count + manifest.BotCount));
        Assert.Equal(humans - remaining, _entryService.Deliveries.Count);
        if (humans >= 8)
            Assert.Contains(_entryService.StoredManifests.Values, manifest => manifest.BotCount == 0);
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

        Assert.Empty(_entryService.Events);
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task RunAsync_OneHumanStartsWithBotsWithoutWaitingForSecondHuman()
    {
        bool soloMapValidation = false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueData alone = UserServerMatchingTestData.HumanEntry(1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, alone, now - 100);

        await CreatePass(soloMapValidation).RunAsync();

        Assert.Equal(7, Assert.Single(_entryService.StoredManifests).Value.BotCount);
        Assert.Equal(0, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }
}
