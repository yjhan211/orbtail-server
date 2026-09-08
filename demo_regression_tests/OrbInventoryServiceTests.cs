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
    public void SummonAndDestroyApplyCurrencyAndInventoryOnce()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984301);
        using (store.Enter(runtime))
        {
            int cost = runtime.SummonStones.GetSnapshot(1).NextCost;
            runtime.SummonStones.AddStones(1, cost);
            var service = new OrbInventoryService(TestGameEventLogs.Create());
            var attempt = service.Summon(runtime, 1, AreaType.None);
            Assert.True(attempt.Success);
            Assert.Equal(0, attempt.State.StoneCount);
            long uid = attempt.AddedItem!.ItemUid;
            int before = runtime.SummonStones.GetSnapshot(1).StoneCount;
            var result = service.Destroy(runtime, 1, uid, AreaType.None);
            Assert.Equal(ErrorCode.SUCCESS, result.Error);
            Assert.Equal(before + Config.SWARM_ORB_DESTROY_REFUND_STONES, result.State.StoneCount);
            var again = service.Destroy(runtime, 1, uid, AreaType.None);
            Assert.Equal(ErrorCode.ITEM_NOT_FOUND, again.Error);
            Assert.Equal(result.State.StoneCount, again.State.StoneCount);
            runtime.TryMarkTerminal();
        }
    }

    [Fact]
    public void GrantKeepsIndependentOrbUids()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984302);
        var service = new OrbInventoryService(TestGameEventLogs.Create());
        Assert.Throws<InvalidOperationException>(() => service.Grant(runtime, 1, 107000010));
        using (store.Enter(runtime))
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
