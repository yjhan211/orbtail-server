using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class GuardianOrbMergeTests
{
    public GuardianOrbMergeTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public void T1GuardianOrbMergeHasOneDeterministicT2Candidate()
    {
        var recipe = Assert.Single(BattleItemRecipeData.GetMatchingRecipes([107000003, 107000003]));

        Assert.Equal(107000004, recipe.OutputItemId);
        Assert.Equal("orb", recipe.Category);
        Assert.Equal("primary", recipe.RouteType);
    }

    [Fact]
    public void T2GuardianOrbsMergeIntoT3InEveryArea()
    {
        foreach (var area in Enum.GetValues<AreaType>())
        {
            var recipe = BattleItemRecipeData.TryCombine([107000004, 107000004], area);
            Assert.NotNull(recipe);
            Assert.Equal(107000006, recipe.OutputItemId);
            Assert.Equal("orb", recipe.Category);
            Assert.Equal("primary", recipe.RouteType);
        }
    }

    [Fact]
    public void GuardianOrbMergeConsumesTwoInputsAndAddsOneOutputAtomically()
    {
        var inventory = new PlayerInGameInventory(193);
        inventory.AddItem(107000003);
        inventory.AddItem(107000003);

        Assert.True(inventory.TryCombineItems([107000003, 107000003], 107000004, out var changedItems));
        Assert.Equal(3, changedItems.Count);
        Assert.Equal(0, inventory.GetItemCount(107000003));
        Assert.Equal(1, inventory.GetItemCount(107000004));

        Assert.False(inventory.TryCombineItems([107000003, 107000003], 107000006, out var rejectedChanges));
        Assert.Empty(rejectedChanges);
        Assert.Equal(1, inventory.GetItemCount(107000004));
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
