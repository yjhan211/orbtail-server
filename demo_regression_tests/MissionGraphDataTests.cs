using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public class MissionGraphDataTests
{
    [Fact]
    public void LibraryMissionGraph_Loads_And_Validates()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var nodes = GameMissionGraphData.GetNodes((short)JobTitle.LIBRARY_COMMITTEE);
        var recipes = GameMissionGraphData.GetRecipes((short)JobTitle.LIBRARY_COMMITTEE);

        Assert.Equal(9, nodes.Count);
        Assert.Equal(5, recipes.Count);
        Assert.Contains(nodes, node => node.NodeId == 3009 && node.AreaType == (int)AreaType.Ground);
        Assert.DoesNotContain(nodes, node => node.NodeKind == MissionGraphNodeKind.GiftSabotage);
        Assert.DoesNotContain(nodes, node => node.NodeKind == MissionGraphNodeKind.RevengeClue);
        Assert.DoesNotContain(nodes, node => node.RequiresTargetLost);
        Assert.All(nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.VisibleTrace.Kr)));
    }

    [Fact]
    public void LibraryMissionGraph_Unlocks_FeatureNodes_From_CollectedParts()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var availableWithWrittenPage = GameMissionGraphData.GetAvailableNodes(
            (short)JobTitle.LIBRARY_COMMITTEE,
            new[] { 305 },
            Array.Empty<int>());

        var availableWithBothToolsOnly = GameMissionGraphData.GetAvailableNodes(
            (short)JobTitle.LIBRARY_COMMITTEE,
            new[] { 305, 306 },
            Array.Empty<int>());

        var availableAfterRecordTasks = GameMissionGraphData.GetAvailableNodes(
            (short)JobTitle.LIBRARY_COMMITTEE,
            new[] { 305, 306 },
            new[] { 3005, 3006 });

        var availableAfterLendingRecords = GameMissionGraphData.GetAvailableNodes(
            (short)JobTitle.LIBRARY_COMMITTEE,
            new[] { 305, 306 },
            new[] { 3005, 3006, 3007 });

        var availableAfterReturnRequests = GameMissionGraphData.GetAvailableNodes(
            (short)JobTitle.LIBRARY_COMMITTEE,
            new[] { 305, 306 },
            new[] { 3005, 3006, 3007, 3008 });

        Assert.Contains(availableWithWrittenPage, node => node.NodeId == 3005);
        Assert.Contains(availableWithBothToolsOnly, node => node.NodeId == 3006);
        Assert.DoesNotContain(availableWithBothToolsOnly, node => node.NodeId == 3007);
        Assert.Contains(availableAfterRecordTasks, node => node.NodeId == 3007);
        Assert.Contains(availableAfterLendingRecords, node => node.NodeId == 3008);
        Assert.Contains(availableAfterReturnRequests, node => node.NodeId == 3009);
    }

    [Fact]
    public void LibraryMissionGraph_Matches_LegacyPartRecipes()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        Assert.True(GameMissionGraphData.TryFindRecipeByPartRecipe(
            (short)JobTitle.LIBRARY_COMMITTEE,
            301,
            302,
            305,
            out var writtenPageRecipe));

        Assert.Equal(3001, writtenPageRecipe.RecipeId);
    }

    private static string FindNetworkBasePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate))
                return Path.Combine(dir.FullName, "network");

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate network/Common/csv from test output path.");
    }
}
