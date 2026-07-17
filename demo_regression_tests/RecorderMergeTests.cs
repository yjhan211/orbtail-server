using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class RecorderMergeTests
{
    public RecorderMergeTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Theory]
    [InlineData(107000003, 107000004, 107000005)]
    [InlineData(107000007, 107000008, 107000009)]
    [InlineData(107000011, 107000012, 107000013)]
    public void T1MagicToolMergeHasTwoT2Candidates(int t1ItemId, int firstT2ItemId, int secondT2ItemId)
    {
        var candidates = BattleItemRecipeData.GetMatchingRecipes([t1ItemId, t1ItemId]);

        Assert.Equal([firstT2ItemId, secondT2ItemId], candidates.Select(recipe => recipe.OutputItemId));
        Assert.All(candidates, recipe => Assert.Equal("random", recipe.RouteType));
    }

    [Theory]
    [InlineData(107000003, 107000004, 107000005)]
    [InlineData(107000007, 107000008, 107000009)]
    [InlineData(107000011, 107000012, 107000013)]
    public void T1MagicToolMergeSelectsBothCandidatesWithEqualWeight(
        int t1ItemId,
        int firstT2ItemId,
        int secondT2ItemId)
    {
        var random = new Random(193);
        var outcomes = Enumerable.Range(0, 1000)
            .Select(_ => BattleItemRecipeData.PickRandomRecipe([t1ItemId, t1ItemId], AreaType.None, random)!.OutputItemId)
            .GroupBy(itemId => itemId)
            .ToDictionary(group => group.Key, group => group.Count());

        Assert.InRange(outcomes[firstT2ItemId], 400, 600);
        Assert.InRange(outcomes[secondT2ItemId], 400, 600);
    }

    [Theory]
    [InlineData(107000004, 107000006, AreaType.StaffRoom)]
    [InlineData(107000005, 107000006, AreaType.StaffRoom)]
    [InlineData(107000008, 107000010, AreaType.Ground)]
    [InlineData(107000009, 107000010, AreaType.Ground)]
    [InlineData(107000012, 107000014, AreaType.Storage2)]
    [InlineData(107000013, 107000014, AreaType.Storage2)]
    public void SameT2MagicToolsMergeIntoT3OnlyInRequiredArea(
        int t2ItemId,
        int t3ItemId,
        AreaType requiredArea)
    {
        var availableRecipe = BattleItemRecipeData.TryCombine([t2ItemId, t2ItemId], requiredArea);

        Assert.NotNull(availableRecipe);
        Assert.Equal(t3ItemId, availableRecipe.OutputItemId);
        Assert.Null(BattleItemRecipeData.TryCombine([t2ItemId, t2ItemId], AreaType.None));
    }

    [Theory]
    [InlineData(107000004, 107000005)]
    [InlineData(107000008, 107000009)]
    [InlineData(107000012, 107000013)]
    public void DifferentT2VariantsCannotMerge(int firstT2ItemId, int secondT2ItemId)
    {
        Assert.Empty(BattleItemRecipeData.GetMatchingRecipes([firstT2ItemId, secondT2ItemId]));
    }

    [Fact]
    public void MagicToolMergeConsumesTwoInputsAndAddsOneOutputAtomically()
    {
        var inventory = new PlayerInGameInventory(193);
        inventory.AddItem(107000003);
        inventory.AddItem(107000003);

        Assert.True(inventory.TryCombineItems([107000003, 107000003], 107000004, out var changedItems));
        Assert.Equal(3, changedItems.Count);
        Assert.Equal(0, inventory.GetItemCount(107000003));
        Assert.Equal(1, inventory.GetItemCount(107000004));

        Assert.False(inventory.TryCombineItems([107000003, 107000003], 107000005, out var rejectedChanges));
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
