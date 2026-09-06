using user_server.matching.creation;
using user_server.matching.queue;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using user_server.matching;

namespace demo_regression_tests;

/// <summary>
///     #323 MatchmakingPass: 8인 그룹 단일 매치, 3인+봇 5 채움, claim 경합 skip, 전달 실패 롤백,
///     전원 success 전달 뒤 admission_ready, 1초 pass의 cutoff·정원 규칙.
/// </summary>
public sealed class MatchmakingPassTests
{
    private readonly InMemoryRedisOperations _cache = new();
    private readonly InMemoryMatchingClaimStore _claimStore;
    private readonly MatchingQueueClaimCoordinator _claims;
    private readonly MatchingQueue _queue;
    private readonly RecordingHandoffPublisher _handoff = new();
    private readonly FixedGameServerAllocator _gameServers = new();
    private readonly RecordingLogger _logger = new();

    public MatchmakingPassTests()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        _claimStore = new InMemoryMatchingClaimStore(_cache);
        _claims = new MatchingQueueClaimCoordinator(_cache, _claimStore, _logger);
        _queue = new MatchingQueue(_cache, new FakeRedLockFactory(), _claims, _logger);
    }

    private MatchmakingPass CreatePass(DevMatchOverrides? overrides = null, CancellationToken shutdown = default)
    {
        overrides ??= new DevMatchOverrides(false, false, _cache, new FakeRedLockFactory(), _logger);
        var rosterBuilder = new MatchRosterBuilder(_cache, _logger);
        return new MatchmakingPass(_cache, _queue, _claims, rosterBuilder, _handoff, _gameServers, overrides, shutdown, _logger);
    }

    private async Task<MatchingQueueEntry[]> EnqueueHumansAsync(int count, double score = 1)
    {
        var entries = new MatchingQueueEntry[count];
        for (int i = 0; i < count; i++)
        {
            entries[i] = UserServerMatchingTestData.HumanEntry(1_000 + i);
            await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, entries[i].Raw, score + i);
        }

        return entries;
    }

    private string? ClaimOf(long playerId) => _cache.GetString(MatchingHandoffRedisKeys.ClaimKey(playerId));

    [Fact]
    public async Task CreateMatchAsync_WithoutGameServerNodeLeavesQueueAndClaimsUntouched()
    {
        MatchingQueueEntry[] humans = await EnqueueHumansAsync(8);
        _gameServers.Allocation = null;

        bool committed = await CreatePass().CreateMatchAsync(humans, 0, MatchCreationOrigin.Queue);

        Assert.False(committed);
        Assert.Equal(1, _gameServers.Calls);
        Assert.Equal(8, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.Null(_cache.GetString(MatchmakingPass.MatchingIdKey));
        Assert.All(humans, human => Assert.Null(ClaimOf(human.PlayerId)));
        Assert.Empty(_handoff.Events);
    }

    [Fact]
    public async Task CreateMatchAsync_EightHumansMakeOneMatchWithoutBots()
    {
        MatchingQueueEntry[] humans = await EnqueueHumansAsync(8);

        bool committed = await CreatePass().CreateMatchAsync(humans, 0, MatchCreationOrigin.Queue);

        Assert.True(committed);
        Assert.Equal("1", _cache.GetString(MatchmakingPass.MatchingIdKey));
        Assert.Empty(_handoff.StoredManifests[1].BotPlayerIds);
        Assert.Equal(humans.Select(h => h.PlayerId).OrderBy(id => id), _handoff.StoredManifests[1].HumanPlayerIds.OrderBy(id => id));
        Assert.Equal(8, _handoff.Deliveries.Count);
        Assert.All(_handoff.Deliveries, delivery => Assert.Equal(8, delivery.RosterCount));
        Assert.All(_handoff.DeliveredNodeIds, nodeId => Assert.Equal(FixedGameServerAllocator.DefaultNodeId, nodeId));
        Assert.Equal(humans.Select(h => h.PlayerId).OrderBy(id => id), _handoff.Deliveries.Select(d => d.Entry.PlayerId).OrderBy(id => id));
        Assert.All(humans, human => Assert.Equal("1", ClaimOf(human.PlayerId)));
        Assert.Equal(0, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.Contains("ready:1", _handoff.Events);
        Assert.Contains($"watchdog:1:{string.Join(",", humans.Select(h => h.PlayerId))}", _handoff.Events);
        Assert.DoesNotContain(_handoff.Events, e => e.StartsWith("cancel:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateMatchAsync_ThreeHumansAreFilledWithFiveBots()
    {
        MatchingQueueEntry[] humans = await EnqueueHumansAsync(3);

        bool committed = await CreatePass().CreateMatchAsync(humans, 5, MatchCreationOrigin.Queue);

        Assert.True(committed);
        network.common.data.models.MatchManifest manifest = _handoff.StoredManifests[1];
        Assert.Equal(5, manifest.BotPlayerIds.Count);
        Assert.All(manifest.BotPlayerIds, botId => Assert.True(botId < 0));
        Assert.Equal(5, manifest.BotPlayerIds.Distinct().Count());
        Assert.Equal(3, manifest.HumanPlayerIds.Count);
        Assert.Equal(3, _handoff.Deliveries.Count);
        Assert.All(_handoff.Deliveries, delivery => Assert.Equal(8, delivery.RosterCount));
        Assert.Equal("1", ClaimOf(1_000));
        Assert.Contains("ready:1", _handoff.Events);
    }

    [Fact]
    public async Task CreateMatchAsync_SuccessPacketsReachEveryHumanBeforeAdmissionReady()
    {
        MatchingQueueEntry[] humans = await EnqueueHumansAsync(4);

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
    public async Task CreateMatchAsync_RejectedDeliveryRollsBackClaimsHandoffAndNotifiesBatch()
    {
        MatchingQueueEntry[] humans = await EnqueueHumansAsync(3);
        _handoff.DeliverResult = playerId => playerId != 1_001;

        bool committed = await CreatePass().CreateMatchAsync(humans, 5, MatchCreationOrigin.Queue);

        Assert.False(committed);
        Assert.DoesNotContain("ready:1", _handoff.Events);
        Assert.Contains("cancel:1", _handoff.Events);
        Assert.Contains("delete:1", _handoff.Events);
        Assert.Contains("notify_failed:1:1000,1001,1002", _handoff.Events);
        Assert.All(humans, human => Assert.Null(ClaimOf(human.PlayerId)));
        Assert.Equal(0, _cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.True(_logger.Contains(LogLevel.Warning, "Matching rolled back because delivery was incomplete"));
    }

    [Fact]
    public async Task CreateMatchAsync_DeliveryExceptionIsContainedAndRolledBack()
    {
        MatchingQueueEntry[] humans = await EnqueueHumansAsync(2);
        _handoff.ThrowOnDeliver.Add(1_000);

        bool committed = await CreatePass().CreateMatchAsync(humans, 6, MatchCreationOrigin.BotFill);

        Assert.False(committed);
        Assert.Equal(2, _handoff.Deliveries.Count);
        Assert.Contains("cancel:1", _handoff.Events);
        Assert.All(humans, human => Assert.Null(ClaimOf(human.PlayerId)));
        Assert.True(_logger.Contains(LogLevel.Error, "Failed to commit matching entry"));
    }

    [Fact]
    public async Task CreateMatchAsync_CompletedAdmissionIsNotRolledBack()
    {
        MatchingQueueEntry[] humans = await EnqueueHumansAsync(2);
        _handoff.DeliverResult = playerId => playerId != 1_001;
        _handoff.CancelAdmissionResult = false;

        bool committed = await CreatePass().CreateMatchAsync(humans, 6, MatchCreationOrigin.Queue);

        Assert.False(committed);
        Assert.Contains("cancel:1", _handoff.Events);
        Assert.DoesNotContain("delete:1", _handoff.Events);
        Assert.DoesNotContain(_handoff.Events, e => e.StartsWith("notify_failed:", StringComparison.Ordinal));
        // 전달된 사람의 active claim은 그대로 남고(TTL fallback), 거절된 사람의 claim만 즉시 해제된다.
        Assert.Equal("1", ClaimOf(1_000));
        Assert.Null(ClaimOf(1_001));
    }

    [Fact]
    public async Task CreateMatchAsync_ClaimContentionSkipsGroupWithoutIssuingMatchingId()
    {
        MatchingQueueEntry[] humans = await EnqueueHumansAsync(2);
        await _cache.StringSetAsync(MatchingHandoffRedisKeys.ClaimKey(1_001), "other-worker");

        bool committed = await CreatePass().CreateMatchAsync(humans, 6, MatchCreationOrigin.Queue);

        Assert.False(committed);
        Assert.Null(_cache.GetString(MatchmakingPass.MatchingIdKey));
        Assert.Empty(_handoff.Events);
        Assert.Null(ClaimOf(1_000));
        Assert.Equal("other-worker", ClaimOf(1_001));
        Assert.Equal(2, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task CreateMatchAsync_WatchdogRefusalDuringShutdownRollsBackAfterReady()
    {
        MatchingQueueEntry[] humans = await EnqueueHumansAsync(1);
        _handoff.WatchdogResult = false;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreatePass().CreateMatchAsync(humans, 7, MatchCreationOrigin.Queue));

        Assert.Contains("ready:1", _handoff.Events);
        Assert.Contains("cancel:1", _handoff.Events);
        Assert.Null(ClaimOf(1_000));
    }

    [Fact]
    public async Task RunAsync_DefaultRulesMatchEachWaitingHumanSeparatelyWithSevenBots()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueEntry waitedA = UserServerMatchingTestData.HumanEntry(1);
        MatchingQueueEntry waitedB = UserServerMatchingTestData.HumanEntry(2);
        MatchingQueueEntry fresh = UserServerMatchingTestData.HumanEntry(3);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, waitedA.Raw, now - 10);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, waitedB.Raw, now - 9);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, fresh.Raw, now);

        await CreatePass().RunAsync();

        Assert.Equal("2", _cache.GetString(MatchmakingPass.MatchingIdKey));
        Assert.Equal(2, _handoff.StoredManifests.Count);
        Assert.All(_handoff.StoredManifests.Values, manifest => Assert.Equal(7, manifest.BotPlayerIds.Count));
        Assert.Equal(new long[] { 1, 2 }, _handoff.Deliveries.Select(d => d.Entry.PlayerId));
        Assert.Equal("1", ClaimOf(1));
        Assert.Equal("2", ClaimOf(2));
        Assert.Null(ClaimOf(3));
        Assert.True(_cache.SortedSetContains(MatchingQueue.QueueKey, fresh.Raw));
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task RunAsync_SoloMapValidationPublishesModeWithoutBots()
    {
        var overrides = new DevMatchOverrides(false, true, _cache, new FakeRedLockFactory(), _logger);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueEntry player = UserServerMatchingTestData.HumanEntry(1);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, player.Raw, now - 10);

        await CreatePass(overrides).RunAsync();

        MatchManifest manifest = Assert.Single(_handoff.StoredManifests).Value;
        Assert.Equal(MatchMode.SoloMapValidation, manifest.Mode);
        Assert.Equal(new long[] { player.PlayerId }, manifest.HumanPlayerIds);
        Assert.Empty(manifest.BotPlayerIds);
        Assert.Single(_handoff.Deliveries);
    }

    [Fact]
    public async Task RunAsync_EmptyQueueDoesNothing()
    {
        await CreatePass().RunAsync();

        Assert.Null(_cache.GetString(MatchmakingPass.MatchingIdKey));
        Assert.Empty(_handoff.Events);
    }

    [Fact]
    public async Task RunAsync_ShutdownTokenStopsBeforeAnyGroup()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueEntry waited = UserServerMatchingTestData.HumanEntry(1);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, waited.Raw, now - 10);
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
        MatchingQueueEntry alone = UserServerMatchingTestData.HumanEntry(1);
        await _cache.SortedSetAddAsync(MatchingQueue.QueueKey, alone.Raw, now - 100);

        await CreatePass(overrides).RunAsync();

        Assert.Empty(_handoff.Events);
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }
}
