using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using user_server.matching.queue;

namespace server_tests;

public sealed class MatchingAssignmentServiceTests
{
    private readonly InMemoryRedisOperations _cache = new();
    private MatchingAssignmentService CreateService() => new(_cache, NullLogger<MatchingAssignmentService>.Instance);

    [Fact]
    public async Task Assign_UsesMatchingIdImmediatelyWithoutCommit()
    {
        var request = UserServerMatchingTestData.HumanEntry(1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, request, 1);

        bool acquired = await CreateService().TryAssignAsync([request], 42);

        Assert.True(acquired);
        Assert.Equal("42", _cache.GetString(MatchingRedisKeys.ReservationKey(1)));
        Assert.Equal(MatchingRedisKeys.EntryReservationLifetime, _cache.Expiries[MatchingRedisKeys.ReservationKey(1)]);
        Assert.Equal(0, _cache.StringSetIfEqualsCalls);
    }

    [Fact]
    public async Task Assign_MissingLaterRequestRollsBackEarlierReservation()
    {
        var first = UserServerMatchingTestData.HumanEntry(1);
        var removed = UserServerMatchingTestData.HumanEntry(2);
        await UserServerMatchingTestData.AddEntryAsync(_cache, first, 1);

        bool acquired = await CreateService().TryAssignAsync([first, removed], 42);

        Assert.False(acquired);
        Assert.Null(_cache.GetString(MatchingRedisKeys.ReservationKey(1)));
        Assert.Null(_cache.GetString(MatchingRedisKeys.ReservationKey(2)));
        Assert.Equal(1, _cache.SortedSetCount(MatchingQueue.QueueKey));
    }

    [Fact]
    public async Task ReleaseAssignments_DoesNotDeleteReplacementAssignment()
    {
        var request = UserServerMatchingTestData.HumanEntry(1);
        await UserServerMatchingTestData.AddEntryAsync(_cache, request, 1);
        var service = CreateService();
        bool acquired = await service.TryAssignAsync([request], 42);
        await _cache.StringSetAsync(MatchingRedisKeys.ReservationKey(1), "43");

        Assert.True(acquired);
        await service.ReleaseAssignmentsAsync([1], 42);

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
        Assert.StartsWith("cancel_", cancellation);

        Assert.False(await service.TryAssignAsync([request], 42));
        Assert.Equal(cancellation, _cache.GetString(MatchingRedisKeys.ReservationKey(1)));
        await service.ReleaseCancellationAsync(1, cancellation);
        Assert.True(await service.TryAssignAsync([request], 43));
        Assert.Null(await service.TryAcquireCancellationAsync(1));
    }

    [Fact]
    public async Task ReleaseCancellation_DoesNotDeleteNewMatchReservation()
    {
        var service = CreateService();
        string? cancellation = await service.TryAcquireCancellationAsync(1);
        Assert.NotNull(cancellation);
        await _cache.StringSetAsync(MatchingRedisKeys.ReservationKey(1), "43");

        await service.ReleaseCancellationAsync(1, cancellation);

        Assert.Equal("43", _cache.GetString(MatchingRedisKeys.ReservationKey(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReleaseAssignment_InvalidMatchDoesNotDeleteCurrentAssignment(long matchingId)
    {
        await _cache.StringSetAsync(MatchingRedisKeys.ReservationKey(1), "43");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateService().ReleaseAssignmentAsync(1, matchingId));

        Assert.Equal("43", _cache.GetString(MatchingRedisKeys.ReservationKey(1)));
    }

    [Fact]
    public async Task IsMatchingBlocked_RecognizesBothAssignmentAndCancellation()
    {
        var service = CreateService();
        Assert.False(await service.IsMatchingBlockedAsync(1));
        string? cancellationId = await service.TryAcquireCancellationAsync(1);
        Assert.True(await service.IsMatchingBlockedAsync(1));
        await service.ReleaseCancellationAsync(1, cancellationId!);
        await _cache.StringSetAsync(MatchingRedisKeys.ReservationKey(1), "42");
        Assert.True(await service.IsMatchingBlockedAsync(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Assign_RejectsInvalidMatchingId(long matchingId)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateService().TryAssignAsync([], matchingId));
        Assert.Equal(0, _cache.StringSetIfNotExistsCalls);
    }
}
