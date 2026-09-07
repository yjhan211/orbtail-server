using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MonsterSnapshotPublisherTests
{
    [Fact]
    public void BroadcastSlot_IsIndependentPerMatchAndAllowsBoundary()
    {
        var first = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(947101);
        var second = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(947102);
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        lock (first.Sync)
        {
            Assert.True(MonsterSnapshotPublisher.TryConsumeBroadcastSlot(first, now));
            Assert.False(MonsterSnapshotPublisher.TryConsumeBroadcastSlot(first, now.AddMilliseconds(99)));
            Assert.True(MonsterSnapshotPublisher.TryConsumeBroadcastSlot(first, now.AddMilliseconds(100)));
        }
        lock (second.Sync)
            Assert.True(MonsterSnapshotPublisher.TryConsumeBroadcastSlot(second, now));
    }
}
