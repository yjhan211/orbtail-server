using game_server.matches;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class OrbTrailServiceTests
{
    public OrbTrailServiceTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        if (directory == null)
            throw new DirectoryNotFoundException("Repository root not found.");

        network.common.data.helpers.GameDataHelper.SetBasePath(Path.Combine(directory.FullName, "network"));
        network.common.data.helpers.GameDataHelper.Initialize();
    }

    [Fact]
    public void Trail_InterpolatesAndExtrapolatesWithoutSharingMatches()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(947401);
        var second = store.GetOrCreate(947402);
        var service = new OrbTrailService(store);
        var anchor = new Vector3f(0, 0, 0);
        using (MatchRuntimeStore.Enter(first))
        {
            first.Swarm.TrailCombat.OrbTrails[(first.MatchingId, 11)] =
                [new Vector3f(2, 0, 0), new Vector3f(4, 0, 0)];
            var middle = service.GetSwarmTrailPositionAtDistance(first.MatchingId, 11, 3, anchor);
            var beyond = service.GetSwarmTrailPositionAtDistance(first.MatchingId, 11, 6, anchor);
            Assert.Equal(3f, middle.X);
            Assert.Equal(0f, middle.Y);
            Assert.Equal(6f, beyond.X);
            Assert.Equal(0f, beyond.Y);
        }
        using (MatchRuntimeStore.Enter(second))
        {
            var fallback = service.GetSwarmTrailPositionAtDistance(second.MatchingId, 11, 5, anchor);
            Assert.Equal(0f, fallback.X);
            Assert.Equal(-1f, fallback.Y);
            second.TryMarkTerminal();
        }
        using (MatchRuntimeStore.Enter(first)) first.TryMarkTerminal();
    }

    [Fact]
    public void Destroy_RemovesOnlySuffixAndRejectsInvalidOrdinal()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947403);
        var service = new OrbTrailService(store);
        using (MatchRuntimeStore.Enter(match))
        {
            var inventory = match.Inventory.GetPlayerInventory(11);
            inventory.AddItem(107000010, forceSeparateStack: true);
            inventory.AddItem(107000020, forceSeparateStack: true);
            inventory.AddItem(107000030, forceSeparateStack: true);
            var original = inventory.GetAllItems().OrderBy(item => item.ItemUid).ToArray();
            Assert.Empty(service.DestroySwarmOrbsFromOrdinal(match.MatchingId, 11, -1));
            Assert.Empty(service.DestroySwarmOrbsFromOrdinal(match.MatchingId, 11, 3));
            var removed = service.DestroySwarmOrbsFromOrdinal(match.MatchingId, 11, 1);
            Assert.Equal(original.Skip(1).Select(item => item.ItemUid), removed.Select(item => item.ItemUid));
            Assert.Equal(original[0].ItemUid, Assert.Single(inventory.GetAllItems()).ItemUid);
            match.TryMarkTerminal();
        }
    }

    [Fact]
    public void MonsterSpawnCallback_BelongsToEachMatch()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(947404);
        var second = store.GetOrCreate(947405);
        using (MatchRuntimeStore.Enter(first))
            first.Monsters.FieldSpawnCellResolver = (_, _) => (new Cell(1, 2), new Cell(3, 4));
        using (MatchRuntimeStore.Enter(second))
        {
            Assert.Null(second.Monsters.FieldSpawnCellResolver);
            second.Monsters.FieldSpawnCellResolver = (_, _) => null;
            second.TryMarkTerminal();
        }
        using (MatchRuntimeStore.Enter(first))
        {
            Assert.NotNull(first.Monsters.FieldSpawnCellResolver!(first.MatchingId, AreaType.None));
            first.TryMarkTerminal();
        }
    }
}
