using game_server.monsters;
using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MonsterSnapshotPublisherTests
{
    [Fact]
    public void BroadcastSlot_IsIndependentPerMatchAndAllowsBoundary()
    {
        var first = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947101);
        var second = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947102);
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        lock (first.MatchLock)
        {
            Assert.True(MonsterSnapshotPublisher.TryConsumeBroadcastSlot(first, now));
            Assert.False(MonsterSnapshotPublisher.TryConsumeBroadcastSlot(first, now.AddMilliseconds(99)));
            Assert.True(MonsterSnapshotPublisher.TryConsumeBroadcastSlot(first, now.AddMilliseconds(100)));
        }
        lock (second.MatchLock)
            Assert.True(MonsterSnapshotPublisher.TryConsumeBroadcastSlot(second, now));
    }
}
