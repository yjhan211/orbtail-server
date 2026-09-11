using game_server.matches;
using game_server.players;
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
        var items = new MatchGroundItemState(clock);
        var item = Assert.Single(items.SpawnItems(Config.SWARM_MATCH_GROUND_AREA, 0, 0, [itemId]));
        clock.Advance(TimeSpan.FromSeconds(1));
        var player = new Player { Profile = new PlayerInfo { PlayerId = 1 } };

        var outside = new Vector3f(item.PositionX + radius + 0.01f, item.PositionY, 0);
        PlayerPickupService.AddReachableItemsInArea(player, items, Config.SWARM_MATCH_GROUND_AREA, outside, outside);
        Assert.Empty(PlayerPickupService.TakeReachableItems(player));

        var inside = new Vector3f(item.PositionX + radius - 0.01f, item.PositionY, 0);
        PlayerPickupService.AddReachableItemsInArea(player, items, Config.SWARM_MATCH_GROUND_AREA, inside, inside);
        Assert.Single(PlayerPickupService.TakeReachableItems(player));
    }

    [Fact]
    public void LandingBlocksCandidatesUntilExactDeadline()
    {
        var clock = new Clock();
        var items = new MatchGroundItemState(clock);
        var item = Assert.Single(items.SpawnItems(Config.SWARM_MATCH_GROUND_AREA, 0, 0, [Config.KEY_GROUND_ITEM_ID]));
        var player = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
        var at = new Vector3f(item.PositionX, item.PositionY, 0);
        float dx = item.PositionX - item.SpawnOriginX;
        float dy = item.PositionY - item.SpawnOriginY;
        var duration = TimeSpan.FromSeconds(Config.GetGroundItemLandingSeconds(MathF.Sqrt(dx * dx + dy * dy)));
        clock.Advance(duration - TimeSpan.FromTicks(1));
        PlayerPickupService.AddReachableItemsInArea(player, items, Config.SWARM_MATCH_GROUND_AREA, at, at);
        Assert.Empty(PlayerPickupService.TakeReachableItems(player));
        Assert.True(items.IsLanding(item.GroundItemUid));

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(items.IsLanding(item.GroundItemUid));
        Assert.Empty(PlayerPickupService.TakeReachableItems(player)); // 공중에서 지나친 기록이 착지 뒤 살아나지 않는다.
        PlayerPickupService.AddReachableItemsInArea(player, items, Config.SWARM_MATCH_GROUND_AREA, at, at);
        Assert.Single(PlayerPickupService.TakeReachableItems(player));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
