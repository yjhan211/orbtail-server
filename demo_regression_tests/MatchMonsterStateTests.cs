using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchMonsterStateTests
{
    [Fact]
    public void SnapshotSlot_IsIndependentPerMatchAndAllowsBoundary()
    {
        var first = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947101);
        var second = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947102);
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(first.Monsters.TryClaimSnapshotSlot(now));
        Assert.False(first.Monsters.TryClaimSnapshotSlot(now.AddMilliseconds(99)));
        Assert.True(first.Monsters.TryClaimSnapshotSlot(now.AddMilliseconds(100)));
        Assert.True(second.Monsters.TryClaimSnapshotSlot(now));
    }
}
