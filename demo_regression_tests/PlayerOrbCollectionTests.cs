using game_server.players;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class PlayerOrbCollectionTests
{
    public PlayerOrbCollectionTests()
    {
        // 오브 판별에 필요한 데이터를 직접 준비해 다른 테스트의 실행 순서에 의존하지 않는다.
        InitializeBattleCombatData();
    }

    [Fact]
    public void HighestOrbTierFollowsTheWholeInventory()
    {
        var inventory = new PlayerOrbCollection();
        Assert.Equal(0, inventory.GetHighestOrbTier());
        var first = inventory.AddItem(107000010);
        var highest = inventory.AddItem(107000032);
        Assert.Equal(3, inventory.GetHighestOrbTier());
        Assert.True(inventory.TryRemoveItem(highest.ItemUid, 1, out _));
        Assert.Equal(1, inventory.GetHighestOrbTier());
        Assert.True(inventory.TryRemoveItem(first.ItemUid, 1, out _));
        Assert.Equal(0, inventory.GetHighestOrbTier());
    }

    [Fact]
    public void NonOrbItemsAreRejected()
    {
        var inventory = new PlayerOrbCollection();
        Assert.Throws<ArgumentException>(() => inventory.AddItem(401000005));
        Assert.False(inventory.TryAddItemWithCapacity(401000005, 8, out _));
        Assert.Empty(inventory.GetOrderedOrbs());
        Assert.Equal(0, inventory.GetHighestOrbTier());
    }

    [Fact]
    public void CapacityAndRemovalApplyToIndividualOrbs()
    {
        var orbs = new PlayerOrbCollection();
        Assert.True(orbs.TryAddItemWithCapacity(107000010, 1, out var orb));
        Assert.False(orbs.TryAddItemWithCapacity(107000010, 1, out _));
        Assert.True(orbs.TryRemoveItem(orb!.ItemUid, 1, out var removed));
        Assert.Equal(0, removed!.Count);
        Assert.False(orbs.TryRemoveItem(orb.ItemUid, 1, out _));
        Assert.True(orbs.TryAddItemWithCapacity(107000010, 1, out var next));
        Assert.NotEqual(orb.ItemUid, next!.ItemUid);
    }

    private static void InitializeBattleCombatData()
    {
        BattleItemCombatData.Initialize(CsvHelper.LoadCsv(Path.Combine(
            FindRepositoryRoot(), "network", "Common", "csv", "battle_item_combat.csv")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

}
