using game_server.matches;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class GroundItemPickupCandidatesTests
{
    [Fact]
    public void RemovedFreeSummonKeyCannotBePickedUp()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984399);
        using (match.Enter())
        {
            var item = Assert.Single(match.GroundItems.SpawnItems(
                Config.SWARM_MATCH_GROUND_AREA, 0, 0, [Config.KEY_GROUND_ITEM_ID]));
            TestGroundItemLanding.Complete(match.GroundItems);
            var result = GroundItemPickupService.TryPickup(match, 1, Config.SWARM_MATCH_GROUND_AREA,
                At(item, 0, 0), 100, item.GroundItemUid);
            Assert.Equal(GroundItemClaimStatus.Rejected, result.Status);
            Assert.Equal(ErrorCode.ITEM_NOT_USABLE, result.Rejection);
            Assert.NotNull(match.GroundItems.GetItem(item.GroundItemUid));
            Assert.Empty(match.Inventory.GetAllItems(1));
            match.TryMarkEnded();
        }
    }

    [Fact]
    public void SweptPathFindsItemEvenWhenBothEndpointsAreOutsideRadius()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984301);
        using (match.Enter())
        {
            var item = Spawn(match);
            var candidates = new GroundItemPickupCandidates();
            candidates.Record(match.GroundItems, 1, Config.SWARM_MATCH_GROUND_AREA,
                At(item, -5, 0), At(item, 5, 0));
            var candidate = Assert.Single(candidates.Take());
            Assert.Equal(item.GroundItemUid, candidate.GroundItemUid);
            var result = GroundItemPickupService.TryPickup(match, 1, candidate.Area,
                candidate.Position, 100, candidate.GroundItemUid);
            Assert.Equal(GroundItemClaimStatus.Success, result.Status);
            Assert.Empty(candidates.Take());
            match.TryMarkEnded();
        }
    }

    [Fact]
    public void TurningPathDoesNotUseShortcutBetweenFirstAndLastPoint()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984302);
        using (match.Enter())
        {
            var item = Spawn(match);
            var candidates = new GroundItemPickupCandidates();
            candidates.Record(match.GroundItems, 1, Config.SWARM_MATCH_GROUND_AREA, At(item, -5, 0), At(item, -5, 5));
            candidates.Record(match.GroundItems, 1, Config.SWARM_MATCH_GROUND_AREA, At(item, -5, 5), At(item, 5, 5));
            candidates.Record(match.GroundItems, 1, Config.SWARM_MATCH_GROUND_AREA, At(item, 5, 5), At(item, 5, 0));
            Assert.Empty(candidates.Take());
            match.TryMarkEnded();
        }
    }

    [Fact]
    public void SpawnAfterPassingDoesNotRetroactivelyBecomeCandidate()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984303);
        using (match.Enter())
        {
            var candidates = new GroundItemPickupCandidates();
            candidates.Record(match.GroundItems, 1, Config.SWARM_MATCH_GROUND_AREA,
                new Vector3f(-5, 0, 0), new Vector3f(5, 0, 0));
            Spawn(match);
            Assert.Empty(candidates.Take());
            match.TryMarkEnded();
        }
    }

    [Fact]
    public void StationaryPlayerFindsSpawnedItemAndDuplicateSegmentsOnlyYieldOneCandidate()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984304);
        using (match.Enter())
        {
            var item = Spawn(match);
            var candidates = new GroundItemPickupCandidates();
            candidates.Record(match.GroundItems, 1, Config.SWARM_MATCH_GROUND_AREA, At(item, 0, 0), At(item, 0, 0));
            candidates.Record(match.GroundItems, 1, Config.SWARM_MATCH_GROUND_AREA, At(item, 0, 0), At(item, 0, 0));
            Assert.Single(candidates.Take());
            match.TryMarkEnded();
        }
    }

    [Fact]
    public void SourceBlockedItemIsNotRememberedWhenPlayerMovesAway()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984305);
        using (match.Enter())
        {
            var item = Spawn(match, sourcePlayerId: 1);
            var candidates = new GroundItemPickupCandidates();
            candidates.Record(match.GroundItems, 1, Config.SWARM_MATCH_GROUND_AREA, At(item, 0, 0), At(item, 5, 0));
            match.GroundItems.ReleaseSourcePickupBlocks(1, Config.SWARM_MATCH_GROUND_AREA, item.PositionX + 5, item.PositionY);
            Assert.Empty(candidates.Take());
            candidates.Record(match.GroundItems, 1, Config.SWARM_MATCH_GROUND_AREA, At(item, 5, 0), At(item, 0, 0));
            Assert.Single(candidates.Take());
            match.TryMarkEnded();
        }
    }

    private static GroundItemInfo Spawn(MatchRuntime match, long sourcePlayerId = 0)
    {
        var item = Assert.Single(match.GroundItems.SpawnItems(Config.SWARM_MATCH_GROUND_AREA, 0, 0,
            [Config.SUMMON_STONE_GROUND_ITEM_ID], sourcePlayerId));
        TestGroundItemLanding.Complete(match.GroundItems);
        return item;
    }

    private static Vector3f At(GroundItemInfo item, float dx, float dy) => new(item.PositionX + dx, item.PositionY + dy, 0);
}
