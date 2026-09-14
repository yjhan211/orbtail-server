using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using user_server.matching.queue;

namespace demo_regression_tests;

public sealed class MatchingReservationServiceTests
{
    private readonly InMemoryRedisOperations _cache = new();
    private MatchingReservationService CreateService() => new(_cache, NullLogger<MatchingReservationService>.Instance);

    [Fact]
    public async Task Acquire_UsesMatchingIdImmediatelyWithoutCommit()
    {
        var request = UserServerMatchingTestData.HumanEntry(1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, request, 1);

        var lease = await CreateService().TryAcquireAsync([request], 42);

        Assert.NotNull(lease);
        Assert.Equal("42", lease.ReservationId);
        Assert.Equal("42", _cache.GetString(MatchingRedisKeys.ReservationKey(1)));
        Assert.Equal(MatchingRedisKeys.EntryReservationLifetime, _cache.Expiries[MatchingRedisKeys.ReservationKey(1)]);
        Assert.Equal(0, _cache.StringSetIfEqualsCalls);
    }

    [Fact]
    public async Task Acquire_MissingLaterRequestRollsBackEarlierReservation()
    {
        var first = UserServerMatchingTestData.HumanEntry(1);
        var removed = UserServerMatchingTestData.HumanEntry(2);
        await UserServerMatchingTestData.AddEntryAsync(_cache, first, 1);

        var lease = await CreateService().TryAcquireAsync([first, removed], 42);

        Assert.Null(lease);
        Assert.Null(_cache.GetString(MatchingRedisKeys.ReservationKey(1)));
        Assert.Null(_cache.GetString(MatchingRedisKeys.ReservationKey(2)));
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task Rollback_DoesNotDeleteReplacementReservation()
    {
        var request = UserServerMatchingTestData.HumanEntry(1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, request, 1);
        var service = CreateService();
        var lease = await service.TryAcquireAsync([request], 42);
        await _cache.StringSetAsync(MatchingRedisKeys.ReservationKey(1), "43");

        await service.RollbackAsync(lease!);

        Assert.Equal("43", _cache.GetString(MatchingRedisKeys.ReservationKey(1)));
    }

    [Fact]
    public async Task CancellationFirst_BlocksMatchUntilCancellationIsReleased()
    {
        var request = UserServerMatchingTestData.HumanEntry(1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, request, 1);
        var service = CreateService();
        var cancellation = await service.TryAcquireCancellationAsync(1);
        Assert.NotNull(cancellation);
        Assert.StartsWith("cancel_", cancellation.ReservationId);

        Assert.Null(await service.TryAcquireAsync([request], 42));
        Assert.Equal(cancellation.ReservationId, _cache.GetString(MatchingRedisKeys.ReservationKey(1)));
        await service.ReleaseCancellationAsync(cancellation);
        Assert.NotNull(await service.TryAcquireAsync([request], 43));
        Assert.Null(await service.TryAcquireCancellationAsync(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Acquire_RejectsInvalidMatchingId(long matchingId)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateService().TryAcquireAsync([], matchingId));
        Assert.Equal(0, _cache.StringSetIfNotExistsCalls);
    }
}
