using game_server.matches;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.helpers;
namespace demo_regression_tests;

public sealed class PlayerOrbGrowthServiceTests
{
    public PlayerOrbGrowthServiceTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        if (directory == null) throw new DirectoryNotFoundException("Repository root not found.");
        GameDataHelper.SetBasePath(Path.Combine(directory.FullName, "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public void GrowthOperationsRequireMatchLock()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984302);
        var player = new Player { Profile = new network.common.data.models.PlayerInfo { PlayerId = 1 } };
        runtime.RegisterParticipant(player);
        runtime.SummonStones.AddStones(1, 100);
        var service = new PlayerOrbGrowthService(TestGameEventLogs.Create(), NullLogger<PlayerOrbGrowthService>.Instance);
        Assert.Throws<InvalidOperationException>(() => service.Summon(runtime, player));
        Assert.Throws<InvalidOperationException>(() => service.UpgradeOrb(runtime, player, Config.ORB_UPGRADE_GROUP, 107000010));
        Assert.Throws<InvalidOperationException>(() => PlayerOrbGrowthService.GrantStartingSummonStones(runtime, player));
        Assert.Throws<InvalidOperationException>(() => service.GetUpgradeCost(runtime, player, 107000010 / 10));
        Assert.Throws<InvalidOperationException>(() => service.GetOrbUpgradeInfo(runtime, player));
        Assert.Throws<InvalidOperationException>(() => service.GetNextOrbGrowthCost(runtime, player));
        Assert.Throws<InvalidOperationException>(() => service.GetTopOrbCount(runtime));
        Assert.Equal(100, runtime.SummonStones.GetSnapshot(1).StoneCount);
        Assert.Empty(runtime.Inventory.GetAllItems(1));
        Assert.Equal(0, player.GetOrbUpgradeCount(107000010 / 10));
    }

    [Fact]
    public void SummonAppliesCurrencyAndInventoryOnce()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984301);
        var player = new Player { Profile = new network.common.data.models.PlayerInfo { PlayerId = 1 } };
        runtime.RegisterParticipant(player);
        using (MatchRuntimeStore.Enter(runtime))
        {
            int cost = runtime.SummonStones.GetSnapshot(1).NextCost;
            runtime.SummonStones.AddStones(1, cost);
            var service = new PlayerOrbGrowthService(TestGameEventLogs.Create(), NullLogger<PlayerOrbGrowthService>.Instance);
            var attempt = service.Summon(runtime, player);
            Assert.True(attempt.Success);
            Assert.Equal(0, attempt.State.StoneCount);
            Assert.Equal(attempt.AddedItem!.ItemUid,
                Assert.Single(runtime.Inventory.GetAllItems(1)).ItemUid);
            var again = service.Summon(runtime, player);
            Assert.False(again.Success);
            Assert.Equal(ErrorCode.INSUFFICIENT_CURRENCY, again.ErrorCode);
            Assert.Equal(0, again.State.StoneCount);
            Assert.Single(runtime.Inventory.GetAllItems(1));
            runtime.TryMarkEnded();
        }
    }

}
