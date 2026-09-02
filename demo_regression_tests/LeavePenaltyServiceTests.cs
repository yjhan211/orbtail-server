using Microsoft.Extensions.Logging;
using user_server.services;

namespace demo_regression_tests;

/// <summary>
///     #323 LeavePenaltyService: 지연 곡선(30초/회, 상한 300초), 24시간 감쇠 산식, Redis 기록·감소·삭제, 조회 실패 fail-open.
/// </summary>
public sealed class LeavePenaltyServiceTests
{
    private const long Day = LeavePenaltyService.PenaltyDecayIntervalSeconds;
    private readonly InMemoryCacheHelper _cache = new();
    private readonly RecordingLogger _logger = new();
    private readonly LeavePenaltyService _service;

    public LeavePenaltyServiceTests()
    {
        _service = new LeavePenaltyService(_cache, _logger);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 30)]
    [InlineData(5, 150)]
    [InlineData(10, 300)]
    [InlineData(11, 300)]
    [InlineData(1000, 300)]
    public void ComputeQueueDelaySeconds_ThirtyPerLeaveCappedAtFiveMinutes(long leaveCount, long expected)
    {
        Assert.Equal(expected, LeavePenaltyService.ComputeQueueDelaySeconds(leaveCount));
    }

    [Fact]
    public void ComputeDecay_IncompleteIntervalChangesNothing()
    {
        LeavePenaltyDecay decay = LeavePenaltyService.ComputeDecay(3, 1_000, 1_000 + Day - 1);

        Assert.False(decay.Decayed);
        Assert.Equal(3, decay.RemainingCount);
        Assert.Equal(1_000, decay.DecayAtUnixSeconds);
    }

    [Fact]
    public void ComputeDecay_ClockGoingBackwardsChangesNothing()
    {
        LeavePenaltyDecay decay = LeavePenaltyService.ComputeDecay(3, 5_000, 4_000);

        Assert.False(decay.Decayed);
        Assert.Equal(3, decay.RemainingCount);
    }

    [Fact]
    public void ComputeDecay_RemovesOneLeavePerCompletedIntervalAndKeepsFraction()
    {
        LeavePenaltyDecay decay = LeavePenaltyService.ComputeDecay(3, 1_000, 1_000 + Day + 3_600);

        Assert.True(decay.Decayed);
        Assert.Equal(2, decay.RemainingCount);
        Assert.Equal(1_000 + Day, decay.DecayAtUnixSeconds);
    }

    [Fact]
    public void ComputeDecay_ClampsAtZeroAfterManyIntervals()
    {
        LeavePenaltyDecay decay = LeavePenaltyService.ComputeDecay(2, 1_000, 1_000 + 5 * Day);

        Assert.True(decay.Decayed);
        Assert.Equal(0, decay.RemainingCount);
        Assert.Equal(1_000 + 5 * Day, decay.DecayAtUnixSeconds);
    }

    [Fact]
    public async Task GetQueueDelayAsync_NoRecordIsZero()
    {
        Assert.Equal(0, await _service.GetQueueDelayAsync(1));
    }

    [Fact]
    public async Task GetQueueDelayAsync_InitializesDecayAnchorOnFirstLookup()
    {
        await _cache.HashSetAsync(LeavePenaltyService.LeavePenaltyKey, 1, BitConverter.GetBytes(2L));

        long delay = await _service.GetQueueDelayAsync(1);

        Assert.Equal(60, delay);
        byte[]? anchor = _cache.GetHash(LeavePenaltyService.LeavePenaltyDecayAtKey, 1);
        Assert.NotNull(anchor);
        long anchorSeconds = BitConverter.ToInt64(anchor);
        Assert.InRange(anchorSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
    }

    [Fact]
    public async Task GetQueueDelayAsync_FullyDecayedPenaltyRemovesBothKeys()
    {
        long twoDaysAgo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2 * Day - 10;
        await _cache.HashSetAsync(LeavePenaltyService.LeavePenaltyKey, 1, BitConverter.GetBytes(2L));
        await _cache.HashSetAsync(LeavePenaltyService.LeavePenaltyDecayAtKey, 1, BitConverter.GetBytes(twoDaysAgo));

        long delay = await _service.GetQueueDelayAsync(1);

        Assert.Equal(0, delay);
        Assert.Null(_cache.GetHash(LeavePenaltyService.LeavePenaltyKey, 1));
        Assert.Null(_cache.GetHash(LeavePenaltyService.LeavePenaltyDecayAtKey, 1));
        Assert.True(_logger.Contains(LogLevel.Information, "Leave penalty fully decayed"));
    }

    [Fact]
    public async Task GetQueueDelayAsync_PartialDecayWritesRemainingCountAndAdvancedAnchor()
    {
        long oneDayAgo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Day - 100;
        await _cache.HashSetAsync(LeavePenaltyService.LeavePenaltyKey, 1, BitConverter.GetBytes(3L));
        await _cache.HashSetAsync(LeavePenaltyService.LeavePenaltyDecayAtKey, 1, BitConverter.GetBytes(oneDayAgo));

        long delay = await _service.GetQueueDelayAsync(1);

        Assert.Equal(60, delay);
        Assert.Equal(2L, BitConverter.ToInt64(_cache.GetHash(LeavePenaltyService.LeavePenaltyKey, 1)!));
        Assert.Equal(oneDayAgo + Day, BitConverter.ToInt64(_cache.GetHash(LeavePenaltyService.LeavePenaltyDecayAtKey, 1)!));
    }

    [Fact]
    public async Task GetQueueDelayAsync_LookupFailureLogsWarningAndReturnsZero()
    {
        _cache.HashGetError = new TimeoutException("redis down");

        long delay = await _service.GetQueueDelayAsync(1);

        Assert.Equal(0, delay);
        Assert.True(_logger.Contains(LogLevel.Warning, "Leave penalty lookup failed"));
    }

    [Fact]
    public async Task RecordLeaveAsync_IncrementsCountAndSetsAnchorOnlyOnFirstLeave()
    {
        await _service.RecordLeaveAsync(1);
        byte[] firstAnchor = _cache.GetHash(LeavePenaltyService.LeavePenaltyDecayAtKey, 1)!;
        await _cache.HashSetAsync(LeavePenaltyService.LeavePenaltyDecayAtKey, 1, BitConverter.GetBytes(12345L));

        await _service.RecordLeaveAsync(1);

        Assert.NotNull(firstAnchor);
        Assert.Equal(2L, BitConverter.ToInt64(_cache.GetHash(LeavePenaltyService.LeavePenaltyKey, 1)!));
        Assert.Equal(12345L, BitConverter.ToInt64(_cache.GetHash(LeavePenaltyService.LeavePenaltyDecayAtKey, 1)!));
    }

    [Fact]
    public async Task RecordCompletionAsync_DecrementsAndRemovesKeysAtZero()
    {
        await _cache.HashSetAsync(LeavePenaltyService.LeavePenaltyKey, 1, BitConverter.GetBytes(2L));
        await _cache.HashSetAsync(LeavePenaltyService.LeavePenaltyDecayAtKey, 1, BitConverter.GetBytes(1L));

        await _service.RecordCompletionAsync(1);
        Assert.Equal(1L, BitConverter.ToInt64(_cache.GetHash(LeavePenaltyService.LeavePenaltyKey, 1)!));
        Assert.NotNull(_cache.GetHash(LeavePenaltyService.LeavePenaltyDecayAtKey, 1));

        await _service.RecordCompletionAsync(1);
        Assert.Null(_cache.GetHash(LeavePenaltyService.LeavePenaltyKey, 1));
        Assert.Null(_cache.GetHash(LeavePenaltyService.LeavePenaltyDecayAtKey, 1));
    }

    [Fact]
    public async Task RecordCompletionAsync_WithoutPenaltyIsNoOp()
    {
        await _service.RecordCompletionAsync(1);

        Assert.Null(_cache.GetHash(LeavePenaltyService.LeavePenaltyKey, 1));
    }
}
