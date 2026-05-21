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

    [Fact]
    public void MissionGraphNode_Parses_Storylet_Metadata()
    {
        var row = new CsvRow(
            new[]
            {
                "node_id",
                "job_title",
                "node_key",
                "node_kind",
                "storylet_id",
                "storylet_type",
                "route_type",
                "target_area_type",
                "target_object_type",
                "required_output_item_id",
                "recipe_id",
                "clue_tags",
                "final_tags",
                "case_group",
                "stat_check",
                "stat_threshold",
                "claim_policy",
                "reward_kind",
                "risk_level",
                "location_hint_text_id",
                "trace_text_id",
                "contested_text_id",
                "is_victory_storylet"
            },
            new[]
            {
                "4101",
                "3",
                "ROUTE-RECORD-RISK",
                "2",
                "ROUTE-RECORD-RISK",
                "route",
                "risk_high_reward",
                "41",
                "9",
                "9001",
                "3001",
                "record|public_trace",
                "record_final",
                "record",
                "observation",
                "2",
                "unique",
                "mixed",
                "3",
                "12001",
                "12002",
                "12003",
                "false"
            });

        var node = MissionGraphNodeData.CreateFromData(row);

        Assert.True(node.HasStoryletMetadata);
        Assert.Equal("ROUTE-RECORD-RISK", node.EffectiveStoryletId);
        Assert.Equal(MissionGraphStoryletType.Route, node.StoryletType);
        Assert.Equal(MissionGraphRouteType.RiskHighReward, node.RouteType);
        Assert.Equal(41, node.TargetAreaType);
        Assert.Equal(9, node.TargetObjectType);
        Assert.Equal(9001, node.RequiredOutputItemId);
        Assert.Equal(3001, node.RecipeId);
        Assert.Equal(new[] { "record", "public_trace" }, node.ClueTags);
        Assert.Equal(new[] { "record_final" }, node.FinalTags);
        Assert.Equal("record", node.CaseGroup);
        Assert.Equal("observation", node.StatCheck);
        Assert.Equal(2, node.StatThreshold);
        Assert.Equal(MissionGraphClaimPolicy.Unique, node.ClaimPolicy);
        Assert.Equal(MissionGraphRewardKind.Mixed, node.RewardKind);
        Assert.Equal(3, node.RiskLevel);
        Assert.Equal(12001, node.LocationHintTextId);
        Assert.Equal(12002, node.TraceTextId);
        Assert.Equal(12003, node.ContestedTextId);
        Assert.False(node.IsVictoryStorylet);
    }

    [Fact]
    public void MissionGraphNode_Treats_VictoryStoryletType_AsVictory()
    {
        var row = new CsvRow(
            new[] { "node_id", "job_title", "node_key", "node_kind", "storylet_type" },
            new[] { "4201", "3", "STORY-RECORD-VICTORY-01", "2", "victory" });

        var node = MissionGraphNodeData.CreateFromData(row);

        Assert.Equal(MissionGraphStoryletType.Victory, node.StoryletType);
        Assert.True(node.IsVictoryStorylet);
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
