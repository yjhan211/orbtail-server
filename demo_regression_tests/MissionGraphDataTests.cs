using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public class MissionGraphDataTests
{
    [Fact]
    public void RecordStoryletGraph_Loads_And_Validates()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var nodes = GameMissionGraphData.GetNodes(0);
        var recipes = GameMissionGraphData.GetRecipes(0);

        Assert.Equal(11, nodes.Count);
        Assert.Single(recipes);
        Assert.Contains(nodes, node => node.NodeId == 3111);
        Assert.Contains(nodes, node => node.NodeId == 3112);
        Assert.Contains(nodes, node => node.NodeId == 3113);
        Assert.Contains(nodes, node => node.NodeId == 3106 && node.TargetAreaType == (int)AreaType.Ground);
        Assert.DoesNotContain(nodes, node => node.NodeId is >= 3005 and <= 3009);
        Assert.DoesNotContain(nodes, node => node.NodeKind == MissionGraphNodeKind.GiftSabotage);
        Assert.DoesNotContain(nodes, node => node.NodeKind == MissionGraphNodeKind.RevengeClue);
        Assert.DoesNotContain(nodes, node => node.RequiresTargetLost);
        Assert.All(nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.VisibleTrace.Kr)));
    }

    [Fact]
    public void RecordStoryletGraph_Unlocks_Sentences_Before_Routes()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var availableWithWrittenPage = GameMissionGraphData.GetAvailableNodes(
            (short)JobTitle.LIBRARY_COMMITTEE,
            new[] { 305 },
            Array.Empty<int>());

        var availableAfterWritingStarts = GameMissionGraphData.GetAvailableNodes(
            (short)JobTitle.LIBRARY_COMMITTEE,
            new[] { 305 },
            new[] { 3101 });

        var availableAfterOneSentence = GameMissionGraphData.GetAvailableNodes(
            (short)JobTitle.LIBRARY_COMMITTEE,
            new[] { 305 },
            new[] { 3101, 3111 });

        var availableAfterAllSentences = GameMissionGraphData.GetAvailableNodes(
            (short)JobTitle.LIBRARY_COMMITTEE,
            new[] { 305 },
            new[] { 3101, 3111, 3112, 3113 });

        var availableAfterInfoSafeRoutes = GameMissionGraphData.GetAvailableNodes(
            (short)JobTitle.LIBRARY_COMMITTEE,
            new[] { 305 },
            new[] { 3101, 3102, 3103 });

        var availableAfterRecordMerge = GameMissionGraphData.GetAvailableNodes(
            (short)JobTitle.LIBRARY_COMMITTEE,
            new[] { 305 },
            new[] { 3101, 3102, 3103, 3105 });

        Assert.Contains(availableWithWrittenPage, node => node.NodeId == 3101);
        Assert.DoesNotContain(availableWithWrittenPage, node => node.NodeId == 3005);
        Assert.Contains(availableAfterWritingStarts, node => node.NodeId == 3111);
        Assert.Contains(availableAfterWritingStarts, node => node.NodeId == 3112);
        Assert.Contains(availableAfterWritingStarts, node => node.NodeId == 3113);
        Assert.DoesNotContain(availableAfterWritingStarts, node => node.NodeId is 3102 or 3103 or 3104);
        Assert.DoesNotContain(availableAfterOneSentence, node => node.NodeId is 3102 or 3103 or 3104);
        Assert.Contains(availableAfterAllSentences, node => node.NodeId == 3102);
        Assert.Contains(availableAfterAllSentences, node => node.NodeId == 3103);
        Assert.Contains(availableAfterAllSentences, node => node.NodeId == 3104);
        Assert.Contains(availableAfterInfoSafeRoutes, node => node.NodeId == 3105);
        Assert.Contains(availableAfterRecordMerge, node => node.NodeId == 3106);
    }

    [Fact]
    public void RecordStoryletGraph_Matches_CommonPartRecipes()
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

    [Fact]
    public void StoryletNode_Uses_TargetAreaAndObject_ForInteractableMatch()
    {
        var row = new CsvRow(
            new[] { "node_id", "job_title", "node_key", "node_kind", "area_type", "object_type", "target_area_type", "target_object_type" },
            new[] { "4301", "0", "STORY-RECORD-DISCOVER-01", "2", "1", "1", "41", "9" });

        var node = MissionGraphNodeData.CreateFromData(row);

        Assert.True(node.MatchesInteractable(41, 9, 0));
        Assert.False(node.MatchesInteractable(1, 1, 0));
    }

    [Fact]
    public void StoryletNode_Requires_OutputItem_WhenSpecified()
    {
        var row = new CsvRow(
            new[] { "node_id", "job_title", "node_key", "node_kind", "required_output_item_id" },
            new[] { "4302", "0", "ROUTE-RECORD-INFO", "2", "9001" });

        var node = MissionGraphNodeData.CreateFromData(row);

        Assert.False(node.AreRequirementsMet(new HashSet<int>(), new HashSet<int>()));
        Assert.True(node.AreRequirementsMet(new HashSet<int> { 9001 }, new HashSet<int>()));
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
