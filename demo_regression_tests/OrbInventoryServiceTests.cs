using game_server.matches;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.helpers;
namespace demo_regression_tests;

public sealed class OrbInventoryServiceTests
{
    public OrbInventoryServiceTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        if (directory == null) throw new DirectoryNotFoundException("Repository root not found.");
        GameDataHelper.SetBasePath(Path.Combine(directory.FullName, "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public void SummonAppliesCurrencyAndInventoryOnce()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984301);
        using (MatchRuntimeStore.Enter(runtime))
        {
            int cost = runtime.SummonStones.GetSnapshot(1).NextCost;
            runtime.SummonStones.AddStones(1, cost);
            var service = new OrbInventoryService(TestGameEventLogs.Create());
            var attempt = service.Summon(runtime, 1, AreaType.None);
            Assert.True(attempt.Success);
            Assert.Equal(0, attempt.State.StoneCount);
            Assert.Equal(attempt.AddedItem!.ItemUid,
                Assert.Single(runtime.Inventory.GetAllItems(1)).ItemUid);
            var again = service.Summon(runtime, 1, AreaType.None);
            Assert.False(again.Success);
            Assert.Equal(ErrorCode.INSUFFICIENT_CURRENCY, again.ErrorCode);
            Assert.Equal(0, again.State.StoneCount);
            Assert.Single(runtime.Inventory.GetAllItems(1));
            runtime.TryMarkTerminal();
        }
    }

    [Fact]
    public void GrantKeepsIndependentOrbUids()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984302);
        var service = new OrbInventoryService(TestGameEventLogs.Create());
        Assert.Throws<InvalidOperationException>(() => service.Grant(runtime, 1, 107000010));
        using (MatchRuntimeStore.Enter(runtime))
        {
            service.Grant(runtime, 1, 107000010);
            service.Grant(runtime, 1, 107000010);
            var items = runtime.Inventory.GetPlayerInventory(1).GetAllItems();
            Assert.Equal(2, items.Count);
            Assert.Equal(2, items.Select(item => item.ItemUid).Distinct().Count());
            runtime.TryMarkTerminal();
        }
    }
}
