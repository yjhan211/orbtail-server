using game_server.items;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class GroundItemLandingTests
{
    [Theory]
    [InlineData(Config.KEY_GROUND_ITEM_ID, 1.05f)]
    [InlineData(Config.SUMMON_STONE_GROUND_ITEM_ID, 1.6f)]
    public void PickupUsesReducedRadiusAfterLanding(int itemId, float radius)
    {
        var clock = new Clock();
        var items = new GroundItemManager(984401, clock);
        var item = Assert.Single(items.SpawnItems(Config.SWARM_MATCH_GROUND_AREA, 0, 0, [itemId]));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(GroundItemClaimStatus.TooFar, items.TryClaim(item.GroundItemUid, 1,
            Config.SWARM_MATCH_GROUND_AREA, item.PositionX + radius + 0.01f, item.PositionY, _ => true, out _));
        Assert.Equal(GroundItemClaimStatus.Success, items.TryClaim(item.GroundItemUid, 1,
            Config.SWARM_MATCH_GROUND_AREA, item.PositionX + radius - 0.01f, item.PositionY, _ => true, out _));
    }

    [Fact]
    public void LandingBlocksBothCandidatesAndClaimUntilExactDeadline()
    {
        var clock = new Clock();
        var items = new GroundItemManager(984402, clock);
        var item = Assert.Single(items.SpawnItems(Config.SWARM_MATCH_GROUND_AREA, 0, 0, [Config.KEY_GROUND_ITEM_ID]));
        var candidates = new GroundItemPickupCandidates();
        var at = new Vector3f(item.PositionX, item.PositionY, 0);
        float dx = item.PositionX - item.SpawnOriginX;
        float dy = item.PositionY - item.SpawnOriginY;
        var duration = TimeSpan.FromSeconds(Config.GetGroundItemLandingSeconds(MathF.Sqrt(dx * dx + dy * dy)));
        clock.Advance(duration - TimeSpan.FromTicks(1));
        candidates.Record(items, 1, Config.SWARM_MATCH_GROUND_AREA, at, at);
        Assert.Empty(candidates.Take());
        Assert.Equal(GroundItemClaimStatus.Landing, items.TryClaim(item.GroundItemUid, 1,
            Config.SWARM_MATCH_GROUND_AREA, at.X, at.Y, _ => throw new Exception("Must not apply before landing"), out _));

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(items.IsLanding(item.GroundItemUid));
        Assert.Empty(candidates.Take()); // 공중에서 지나친 기록이 착지 뒤 살아나지 않는다.
        candidates.Record(items, 1, Config.SWARM_MATCH_GROUND_AREA, at, at);
        Assert.Single(candidates.Take());
        Assert.Equal(GroundItemClaimStatus.Success, items.TryClaim(item.GroundItemUid, 1,
            Config.SWARM_MATCH_GROUND_AREA, at.X, at.Y, _ => true, out _));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
