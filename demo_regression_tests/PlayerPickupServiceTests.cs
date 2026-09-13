using game_server.matches;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerPickupServiceTests
{
    [Fact]
    public void RemovedFreeSummonKeyCannotBePickedUp()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984399);
        using (match.Enter())
        {
            var item = Assert.Single(match.GroundItems.SpawnItems(
                AreaType.S2Corridor9, 0, 0, [Config.KEY_GROUND_ITEM_ID]));
            TestGroundItemLanding.Complete(match.GroundItems);
            var player = new Player
            {
                Profile = new PlayerInfo { PlayerId = 1 },
                CurrentArea = AreaType.S2Corridor9,
                Position = At(item, 0, 0)
            };
            match.RegisterParticipant(player);
            PlayerPickupService.AddReachableItemsInArea(
                player,
                match.GroundItems,
                player.CurrentArea,
                player.Position,
                player.Position);

            CreateService(store).PickUp(match, player);

            Assert.NotNull(match.GroundItems.GetItem(item.GroundItemUid));
            Assert.Empty(TestGameSessionServices.Orbs(match, 1).GetAllItems());
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
            var player = new Player
            {
                Profile = new PlayerInfo { PlayerId = 1 },
                CurrentArea = AreaType.S2Corridor9,
                Position = At(item, 5, 0)
            };
            match.RegisterParticipant(player);
            PlayerPickupService.AddReachableItemsInArea(player, match.GroundItems, AreaType.S2Corridor9,
                At(item, -5, 0), At(item, 5, 0));
            var candidate = Assert.Single(PlayerPickupService.TakeReachableItems(player));
            Assert.Equal(item.GroundItemUid, candidate.GroundItemUid);

            player.ReachableItems.TryAdd(candidate.GroundItemUid, candidate);
            CreateService(store).PickUp(match, player);

            Assert.Null(match.GroundItems.GetItem(item.GroundItemUid));
            Assert.Empty(PlayerPickupService.TakeReachableItems(player));
            match.TryMarkEnded();
        }
    }

    [Fact]
    public void PickupRequiresOwningMatchLock()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984201);
        var player = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
        var service = CreateService(store);

        Assert.Throws<InvalidOperationException>(() => service.PickUp(match, player));

        using (match.Enter())
        {
            match.TryMarkEnded();
        }
    }

    [Fact]
    public void MissingItemDoesNotChangePlayerState()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984202);
        var player = new Player
        {
            Profile = new PlayerInfo { PlayerId = 1 },
            CurrentArea = AreaType.S2Corridor9,
            Position = new Vector3f(0, 0, 0)
        };
        player.ReachableItems.TryAdd(
            long.MaxValue,
            new Player.ReachableItem(long.MaxValue, player.CurrentArea, player.Position));
        int healthBeforePickup = player.Health;

        using (match.Enter())
        {
            CreateService(store).PickUp(match, player);

            Assert.Equal(healthBeforePickup, player.Health);
            Assert.Empty(match.GetOrbs(player.PlayerId).GetAllItems());
            Assert.Empty(player.ReachableItems);
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
            var player = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
            PlayerPickupService.AddReachableItemsInArea(player, match.GroundItems, AreaType.S2Corridor9, At(item, -5, 0), At(item, -5, 5));
            PlayerPickupService.AddReachableItemsInArea(player, match.GroundItems, AreaType.S2Corridor9, At(item, -5, 5), At(item, 5, 5));
            PlayerPickupService.AddReachableItemsInArea(player, match.GroundItems, AreaType.S2Corridor9, At(item, 5, 5), At(item, 5, 0));
            Assert.Empty(PlayerPickupService.TakeReachableItems(player));
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
            var player = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
            PlayerPickupService.AddReachableItemsInArea(player, match.GroundItems, AreaType.S2Corridor9,
                new Vector3f(-5, 0, 0), new Vector3f(5, 0, 0));
            Spawn(match);
            Assert.Empty(PlayerPickupService.TakeReachableItems(player));
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
            var player = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
            PlayerPickupService.AddReachableItemsInArea(player, match.GroundItems, AreaType.S2Corridor9, At(item, 0, 0), At(item, 0, 0));
            PlayerPickupService.AddReachableItemsInArea(player, match.GroundItems, AreaType.S2Corridor9, At(item, 0, 0), At(item, 0, 0));
            Assert.Single(PlayerPickupService.TakeReachableItems(player));
            match.TryMarkEnded();
        }
    }

    [Fact]
    public void HeartStaysOnGroundAtFullHealthAndHealsWhenDamaged()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984303);
        using (match.Enter())
        {
            var item = Assert.Single(match.GroundItems.SpawnItems(AreaType.S2Corridor9, 0, 0,
                [PlayerPickupService.HeartItemId]));
            TestGroundItemLanding.Complete(match.GroundItems);
            var player = new Player
            {
                Profile = new PlayerInfo { PlayerId = 1 },
                CurrentArea = AreaType.S2Corridor9,
                Position = At(item, 0, 0),
                Health = Config.MAX_HEALTH
            };
            match.RegisterParticipant(player);
            var service = CreateService(store);

            PlayerPickupService.AddReachableItemsInArea(player, match.GroundItems, player.CurrentArea, player.Position, player.Position);
            service.PickUp(match, player);
            Assert.NotNull(match.GroundItems.GetItem(item.GroundItemUid));
            Assert.Equal(Config.MAX_HEALTH, player.Health);

            player.Health = Config.MAX_HEALTH - 1;
            PlayerPickupService.AddReachableItemsInArea(player, match.GroundItems, player.CurrentArea, player.Position, player.Position);
            service.PickUp(match, player);
            Assert.Null(match.GroundItems.GetItem(item.GroundItemUid));
            Assert.Equal(Config.MAX_HEALTH, player.Health);
            Assert.Empty(TestGameSessionServices.Orbs(match, 1).GetAllItems());
            match.TryMarkEnded();
        }
    }

    private static GroundItemInfo Spawn(MatchRuntime match)
    {
        var item = Assert.Single(match.GroundItems.SpawnItems(AreaType.S2Corridor9, 0, 0,
            [Config.SUMMON_STONE_GROUND_ITEM_ID]));
        TestGroundItemLanding.Complete(match.GroundItems);
        return item;
    }

    private static PlayerPickupService CreateService(MatchRuntimeStore store) =>
        new(
            TestGameSessionServices.CreateHealthService(store),
            NullLogger<PlayerPickupService>.Instance);

    private static Vector3f At(GroundItemInfo item, float dx, float dy) => new(item.PositionX + dx, item.PositionY + dy, 0);
}
