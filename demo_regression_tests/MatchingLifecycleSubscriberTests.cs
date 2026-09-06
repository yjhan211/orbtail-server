using MessagePack;
using network.common;
using network.common.data.models;
using network.infrastructure;
using network.infrastructure.messaging;
using user_server.matching;
using user_server.sessions;

namespace demo_regression_tests;

public sealed class MatchingLifecycleSubscriberTests
{
    [Theory]
    [InlineData(MatchingLifecycleSubjects.PlayerLeft, false)]
    [InlineData(MatchingLifecycleSubjects.PlayerCompleted, false)]
    [InlineData(MatchingLifecycleSubjects.PlayerReleased, false)]
    [InlineData(MatchingLifecycleSubjects.PlayerEntryFailed, true)]
    public async Task MessagePackNotification_RoutesExactPlayerAndMatch(string subject, bool entryFailed)
    {
        var ports = new RecordingPorts();
        using var tracker = new BackgroundTaskTracker(new RecordingLogger());
        var subscriber = new MatchingLifecycleSubscriber(ports, ports, ports, tracker, new RecordingLogger().For<MatchingLifecycleSubscriber>());
        subscriber.Start();
        Assert.Equal(4, ports.Handlers.Count);
        Assert.Single(ports.QueueGroups.Distinct());
        Assert.All(ports.QueueGroups, group => Assert.False(string.IsNullOrWhiteSpace(group)));
        ports.Handlers[subject](subject, MessagePackSerializer.Serialize(
            new G_TO_U_MATCHING_LIFECYCLE { PlayerId = 101, MatchingId = 42 }));
        await tracker.DrainAsync();
        Assert.Equal(new[] { (101L, 42L) }, entryFailed ? ports.Failures : ports.Releases);
        if (entryFailed)
        {
            Assert.Empty(ports.Clears);
            Assert.Empty(ports.Releases);
        }
        else
        {
            Assert.Equal(new[] { (101L, 42L) }, ports.Clears);
            Assert.Empty(ports.Failures);
        }
    }

    [Fact]
    public async Task InvalidOrLegacyPayload_DoesNotClearSessionOrReleaseReservation()
    {
        var ports = new RecordingPorts();
        using var tracker = new BackgroundTaskTracker(new RecordingLogger());
        var subscriber = new MatchingLifecycleSubscriber(ports, ports, ports, tracker, new RecordingLogger().For<MatchingLifecycleSubscriber>());
        subscriber.Start();
        byte[][] payloads =
        [
            [], [0xc1], [0xc0], new byte[8], new byte[16],
            MessagePackSerializer.Serialize(new { playerId = 101L }),
            MessagePackSerializer.Serialize(new G_TO_U_MATCHING_LIFECYCLE { PlayerId = 0, MatchingId = 42 }),
            MessagePackSerializer.Serialize(new G_TO_U_MATCHING_LIFECYCLE { PlayerId = 101, MatchingId = -1 })
        ];
        foreach (var (subject, handler) in ports.Handlers)
            foreach (var payload in payloads)
                handler(subject, payload);
        await tracker.DrainAsync();
        Assert.Empty(ports.Clears);
        Assert.Empty(ports.Releases);
        Assert.Empty(ports.Failures);
    }

    private sealed class RecordingPorts : INatsClient, IPlayerSessionRouter, IMatchingManager
    {
        public Dictionary<string, Action<string, byte[]>> Handlers { get; } = new();
        public List<string?> QueueGroups { get; } = new();
        public List<(long, long)> Clears { get; } = new();
        public List<(long, long)> Releases { get; } = new();
        public List<(long, long)> Failures { get; } = new();
        public void Subscribe(string subject, Action<string, byte[]> handler, string? queue = null)
        { Handlers.Add(subject, handler); QueueGroups.Add(queue); }
        public void ClearMatchingAssignment(long playerId, long matchingId) => Clears.Add((playerId, matchingId));
        public Task ReleaseMatchingReservationAsync(long playerId, long matchingId)
        { Releases.Add((playerId, matchingId)); return Task.CompletedTask; }
        public Task HandleEntryFailureAsync(long playerId, long matchingId)
        { Failures.Add((playerId, matchingId)); return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Close() { }
        public void Publish(string subject, byte[] message) => throw new NotSupportedException();
        public Task<byte[]> RequestAsync(string subject, byte[] message, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void SubscribeRequest(string subject, Func<string, byte[], CancellationToken, Task<byte[]?>> handler, string? queue = null) => throw new NotSupportedException();
        public Task<bool> DeliverMatchingSuccessAsync(long playerId, string requestId, U_TO_C_MATCHING_SUCCESS result) => throw new NotSupportedException();
        public Task<bool> DeliverMatchingFailedAsync(long playerId, long matchingId, string requestId, ErrorCode errorCode) => throw new NotSupportedException();
        public Task<bool> DeliverEntryFailedAsync(long playerId, long matchingId, ErrorCode errorCode) => throw new NotSupportedException();
        public void AnnounceLogin(long playerId, long generation) => throw new NotSupportedException();
        public Task<ErrorCode> AddToQueue(long playerId, PlayerSession session) => throw new NotSupportedException();
        public Task<ErrorCode> CancelMatching(long playerId) => throw new NotSupportedException();
        public Task<bool> HasReservationAsync(long playerId) => throw new NotSupportedException();
        public Task StopMatchingLoopAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }
}
