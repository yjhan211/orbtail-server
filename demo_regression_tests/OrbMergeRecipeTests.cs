using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class OrbMergeRecipeTests
{

    public OrbMergeRecipeTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Theory]
    [InlineData(107000003)]
    [InlineData(107000004)]
    [InlineData(107000010)]
    [InlineData(107000011)]
    [InlineData(107000020)]
    [InlineData(107000021)]
    [InlineData(107000030)]
    [InlineData(107000031)]
    public void RemovedOrbMergeHasNoRecipe(int itemId)
    {
        Assert.Empty(BattleItemRecipeData.GetMatchingRecipes([itemId, itemId]));
        Assert.False(BattleItemRecipeData.IsRecipeInputItem(itemId));
    }

    [Fact]
    public void BattleItemAndRecoveryRecipesRemainAvailable()
    {
        Assert.Equal(201000019, Assert.Single(BattleItemRecipeData.GetMatchingRecipes([201000008, 201000008])).OutputItemId);
        Assert.Equal(107000041, Assert.Single(BattleItemRecipeData.GetMatchingRecipes([107000040, 107000040])).OutputItemId);
    }

    [Fact]
    public void OrbsKeepSeparateSlotsWithoutMergeRecipes()
    {
        var inventory = new PlayerInGameInventory(193);
        var first = inventory.AddItem(107000010);
        var second = inventory.AddItem(107000010);
        var third = inventory.AddItem(107000010);
        Assert.Equal(3, new[] { first.ItemUid, second.ItemUid, third.ItemUid }.Distinct().Count());
        Assert.Equal(3, inventory.GetAllItems().Count);
        Assert.All(inventory.GetAllItems(), item =>
        {
            Assert.Equal(107000010, item.ItemId);
            Assert.Equal(1, item.Count);
        });
    }

    [Fact]
    public void BattleItemRecipeCsvMatchesBothUnityMirrors()
    {
        string repoRoot = FindRepositoryRoot();
        byte[] canonical = File.ReadAllBytes(
            Path.Combine(repoRoot, "network", "Common", "csv", "battle_item_recipe.csv"));

        Assert.Equal(
            canonical,
            File.ReadAllBytes(Path.Combine(
                repoRoot,
                "client",
                "Assets",
                "Resources",
                "Common",
                "csv",
                "battle_item_recipe.csv")));
        Assert.Equal(
            canonical,
            File.ReadAllBytes(Path.Combine(
                repoRoot,
                "client",
                "Assets",
                "StreamingAssets",
                "Common",
                "csv",
                "battle_item_recipe.csv")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }
}
