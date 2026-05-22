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
        var storyletNodes = nodes
            .Where(node => node.NodeId is >= 4101 and < 4800)
            .ToList();
        var startNodes = storyletNodes
            .Where(node => node.NodeId is >= 4101 and <= 4105)
            .OrderBy(node => node.NodeId)
            .ToList();

        Assert.Equal(95, storyletNodes.Count);
        Assert.Equal(5, recipes.Count);
        Assert.Equal(new[]
        {
            "STORY-RECORD-START-WRITING",
            "STORY-RECORD-START-BROADCAST",
            "STORY-RECORD-START-ATTENDANCE",
            "STORY-RECORD-START-CONFISCATED",
            "STORY-RECORD-START-ROLLCALL"
        }, startNodes.Select(node => node.EffectiveStoryletId));
        Assert.All(storyletNodes, node =>
        {
            Assert.Equal(0, node.JobTitle);
            Assert.Equal("record", node.CaseGroup);
            Assert.Equal(MissionGraphClaimPolicy.Unique, node.ClaimPolicy);
            Assert.False(string.IsNullOrWhiteSpace(node.SuccessText.Kr));
        });
        Assert.All(startNodes, node =>
        {
            Assert.Equal(MissionGraphStoryletType.Discovery, node.StoryletType);
            Assert.Equal(MissionGraphRouteType.None, node.RouteType);
            Assert.Equal(MissionGraphRewardKind.ClueTag, node.RewardKind);
            Assert.Single(node.RequiredPartIds);
        });
        Assert.Equal(15, storyletNodes.Count(node => node.NodeId is >= 4201 and < 4300));
        Assert.Equal(15, storyletNodes.Count(node => node.NodeId is >= 4301 and < 4400));
        Assert.Equal(15, storyletNodes.Count(node => node.NodeId is >= 4401 and < 4500));
        Assert.Equal(15, storyletNodes.Count(node => node.NodeId is >= 4501 and < 4600));
        Assert.Equal(15, storyletNodes.Count(node => node.NodeId is >= 4601 and < 4700));
        Assert.Equal(15, storyletNodes.Count(node => node.NodeId is >= 4701 and < 4800));
        Assert.DoesNotContain(nodes, node => node.NodeId is >= 3100 and < 3200);
        Assert.DoesNotContain(nodes, node => node.NodeKey.StartsWith("LIB-", StringComparison.Ordinal));
        Assert.DoesNotContain(nodes, node => node.NodeKind == MissionGraphNodeKind.GiftSabotage);
        Assert.DoesNotContain(nodes, node => node.NodeKind == MissionGraphNodeKind.RevengeClue);
        Assert.DoesNotContain(nodes, node => node.RequiresTargetLost);
    }

    [Fact]
    public void RecordStoryletGraph_Unlocks_StartClueAndCombinationRoutes()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var availableWithWrittenPage = GameMissionGraphData.GetAvailableNodes(
            0,
            new[] { 305 },
            Array.Empty<int>());

        var availableAfterWritingStarts = GameMissionGraphData.GetAvailableNodes(
            0,
            new[] { 305 },
            new[] { 4101 });

        var availableAfterBroadcastClue = GameMissionGraphData.GetAvailableNodes(
            0,
            new[] { 305 },
            new[] { 4101, 4201 });

        var availableAfterWritingBroadcastCombo = GameMissionGraphData.GetAvailableNodes(
            0,
            new[] { 305 },
            new[] { 4101, 4201, 4301 });

        var availableAfterWritingBroadcastContradiction = GameMissionGraphData.GetAvailableNodes(
            0,
            new[] { 305 },
            new[] { 4101, 4201, 4301, 4401 });

        var availableAfterWritingBroadcastCrosscheck = GameMissionGraphData.GetAvailableNodes(
            0,
            new[] { 305 },
            new[] { 4101, 4201, 4301, 4401, 4501 });

        var availableAfterWritingBroadcastRestore = GameMissionGraphData.GetAvailableNodes(
            0,
            new[] { 305 },
            new[] { 4101, 4201, 4301, 4401, 4501, 4601 });

        Assert.Contains(availableWithWrittenPage, node => node.NodeId == 4101);
        Assert.DoesNotContain(availableWithWrittenPage, node => node.NodeId is >= 4201 and <= 4243);
        Assert.DoesNotContain(availableWithWrittenPage, node => node.NodeId is 4102 or 4103 or 4104 or 4105);

        Assert.Contains(availableAfterWritingStarts, node => node.NodeId == 4201);
        Assert.Contains(availableAfterWritingStarts, node => node.NodeId == 4202);
        Assert.Contains(availableAfterWritingStarts, node => node.NodeId == 4203);
        Assert.DoesNotContain(availableAfterWritingStarts, node => node.NodeId is 4211 or 4212 or 4213);
        Assert.DoesNotContain(availableAfterWritingStarts, node => node.NodeId is >= 4301 and < 4400);

        Assert.Contains(availableAfterBroadcastClue, node => node.NodeId == 4301);
        Assert.DoesNotContain(availableAfterBroadcastClue, node => node.NodeId is 4302 or 4303 or 4311);
        Assert.Contains(availableAfterWritingBroadcastCombo, node => node.NodeId == 4401);
        Assert.Contains(availableAfterWritingBroadcastContradiction, node => node.NodeId == 4501);
        Assert.Contains(availableAfterWritingBroadcastCrosscheck, node => node.NodeId == 4601);
        Assert.Contains(availableAfterWritingBroadcastRestore, node => node.NodeId == 4701 && node.IsVictoryStorylet);
    }

    [Fact]
    public void RecordStoryletGraph_Matches_CommonPartRecipes()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var recipeCases = new[]
        {
            (MaterialA: 301, MaterialB: 302, Output: 305, Recipe: 3001),
            (MaterialA: 303, MaterialB: 304, Output: 306, Recipe: 3002),
            (MaterialA: 308, MaterialB: 309, Output: 315, Recipe: 3003),
            (MaterialA: 310, MaterialB: 311, Output: 316, Recipe: 3004),
            (MaterialA: 312, MaterialB: 313, Output: 317, Recipe: 3005)
        };

        foreach (var recipeCase in recipeCases)
        {
            Assert.True(GameMissionGraphData.TryFindRecipeByPartRecipe(
                0,
                recipeCase.MaterialA,
                recipeCase.MaterialB,
                recipeCase.Output,
                out var recipe));

            Assert.Equal(recipeCase.Recipe, recipe.RecipeId);
            Assert.Equal(0, recipe.JobTitle);
        }
    }

    [Fact]
    public void RecordStoryletGraph_Uses_SevenStoryletMilestones()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var writingBroadcastRoute = new[] { 4101, 4201, 4301, 4401, 4501, 4601, 4701 };
        var routeNodes = writingBroadcastRoute
            .Select(GameMissionGraphData.GetNode)
            .ToList();

        Assert.Equal(7, routeNodes.Count);
        Assert.All(routeNodes, node => Assert.True(node.HasStoryletMetadata));
        Assert.Equal(new[] { 4101, 4201 }, GameMissionGraphData.GetNode(4301).RequiredNodeIds);
        Assert.Equal(new[] { 4301 }, GameMissionGraphData.GetNode(4401).RequiredNodeIds);
        Assert.Equal(new[] { 4601 }, GameMissionGraphData.GetNode(4701).RequiredNodeIds);
        Assert.Equal(MissionGraphStoryletType.Victory, GameMissionGraphData.GetNode(4701).StoryletType);
        Assert.DoesNotContain(GameMissionGraphData.GetNodes(0), node => node.NodeId is >= 3111 and <= 3113);
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
