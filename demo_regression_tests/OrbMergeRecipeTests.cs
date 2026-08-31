using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class OrbMergeRecipeTests
{
    // 현행 소환 풀의 태양 오브 라인 (107000003/4/6 수호 오브 계보는 #274에서 테스트 폐기).
    private const int SunOrbT1 = 107000010;
    private const int SunOrbT2 = 107000011;
    private const int SunOrbT3 = 107000012;

    public OrbMergeRecipeTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public void T1OrbMergeHasOneDeterministicT2Candidate()
    {
        var recipe = Assert.Single(BattleItemRecipeData.GetMatchingRecipes([SunOrbT1, SunOrbT1]));

        Assert.Equal(SunOrbT2, recipe.OutputItemId);
        Assert.Equal("orb", recipe.Category);
        Assert.Equal("primary", recipe.RouteType);
    }

    [Fact]
    public void T2OrbsMergeIntoT3InEveryArea()
    {
        foreach (var area in Enum.GetValues<AreaType>())
        {
            var recipe = BattleItemRecipeData.TryCombine([SunOrbT2, SunOrbT2], area);
            Assert.NotNull(recipe);
            Assert.Equal(SunOrbT3, recipe.OutputItemId);
            Assert.Equal("orb", recipe.Category);
            Assert.Equal("primary", recipe.RouteType);
        }
    }

    [Fact]
    public void OrbMergeConsumesTwoInputsAndAddsOneOutputAtomically()
    {
        var inventory = new PlayerInGameInventory(193);
        inventory.AddItem(SunOrbT1);
        inventory.AddItem(SunOrbT1);

        Assert.True(inventory.TryCombineItems([SunOrbT1, SunOrbT1], SunOrbT2, out var changedItems));
        Assert.Equal(3, changedItems.Count);
        Assert.Equal(0, inventory.GetItemCount(SunOrbT1));
        Assert.Equal(1, inventory.GetItemCount(SunOrbT2));

        Assert.False(inventory.TryCombineItems([SunOrbT1, SunOrbT1], SunOrbT3, out var rejectedChanges));
        Assert.Empty(rejectedChanges);
        Assert.Equal(1, inventory.GetItemCount(SunOrbT2));
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
