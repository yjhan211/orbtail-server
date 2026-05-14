// ReSharper disable All
#pragma warning disable CS8618
#pragma warning disable CS8603

using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;
using Newtonsoft.Json;

namespace network.common.data
{
    public static class GameMissionGraphData
    {
        private static readonly Dictionary<int, MissionGraphNodeData> _nodesById = new();
        private static readonly Dictionary<short, List<MissionGraphNodeData>> _nodesByJob = new();
        private static readonly Dictionary<int, MissionGraphRecipeData> _recipesById = new();
        private static readonly Dictionary<short, List<MissionGraphRecipeData>> _recipesByJob = new();

        public static void Initialize(List<CsvRow> nodeData, List<CsvRow> recipeData)
        {
            _nodesById.Clear();
            _nodesByJob.Clear();
            _recipesById.Clear();
            _recipesByJob.Clear();

            foreach (var row in nodeData)
            {
                var node = MissionGraphNodeData.CreateFromData(row);
                _nodesById[node.NodeId] = node;

                if (!_nodesByJob.TryGetValue(node.JobTitle, out var list))
                {
                    list = new List<MissionGraphNodeData>();
                    _nodesByJob[node.JobTitle] = list;
                }

                list.Add(node);
            }

            foreach (var row in recipeData)
            {
                var recipe = MissionGraphRecipeData.CreateFromData(row);
                _recipesById[recipe.RecipeId] = recipe;

                if (!_recipesByJob.TryGetValue(recipe.JobTitle, out var list))
                {
                    list = new List<MissionGraphRecipeData>();
                    _recipesByJob[recipe.JobTitle] = list;
                }

                list.Add(recipe);
            }

            foreach (var list in _nodesByJob.Values)
                list.Sort((a, b) => a.NodeId.CompareTo(b.NodeId));

            foreach (var list in _recipesByJob.Values)
                list.Sort((a, b) => a.RecipeId.CompareTo(b.RecipeId));
        }

        public static MissionGraphNodeData GetNode(int nodeId) =>
            _nodesById.GetValueOrDefault(nodeId);

        public static MissionGraphRecipeData GetRecipe(int recipeId) =>
            _recipesById.GetValueOrDefault(recipeId);

        public static List<MissionGraphNodeData> GetNodes(short jobTitle) =>
            _nodesByJob.GetValueOrDefault(jobTitle) ?? new List<MissionGraphNodeData>();

        public static List<MissionGraphRecipeData> GetRecipes(short jobTitle) =>
            _recipesByJob.GetValueOrDefault(jobTitle) ?? new List<MissionGraphRecipeData>();

        public static List<MissionGraphNodeData> GetAllNodes() =>
            _nodesById.Values.ToList();

        public static List<MissionGraphRecipeData> GetAllRecipes() =>
            _recipesById.Values.ToList();

        public static List<MissionGraphNodeData> GetInitiallyAvailableNodes(short jobTitle) =>
            GetNodes(jobTitle).Where(node => node.IsInitiallyAvailable()).ToList();

        public static List<MissionGraphNodeData> GetAvailableNodes(
            short jobTitle,
            IEnumerable<int> collectedPartIds,
            IEnumerable<int> completedNodeIds,
            bool hasLostTarget = false)
        {
            var collectedPartSet = new HashSet<int>(collectedPartIds ?? Enumerable.Empty<int>());
            var completedNodeSet = new HashSet<int>(completedNodeIds ?? Enumerable.Empty<int>());

            return GetNodes(jobTitle)
                .Where(node => node.AreRequirementsMet(collectedPartSet, completedNodeSet, hasLostTarget))
                .ToList();
        }

        public static bool TryFindRecipeByPartRecipe(
            short jobTitle,
            int inputPartA,
            int inputPartB,
            int outputPartId,
            out MissionGraphRecipeData graphRecipe)
        {
            foreach (var recipe in GetRecipes(jobTitle))
            {
                if (recipe.OutputPartId != outputPartId || recipe.InputPartIds.Count != 2)
                    continue;

                if (recipe.InputPartIds.Contains(inputPartA) && recipe.InputPartIds.Contains(inputPartB))
                {
                    graphRecipe = recipe;
                    return true;
                }
            }

            graphRecipe = null;
            return false;
        }
    }

    public enum MissionGraphNodeKind
    {
        CollectPart = 1,
        UseFeature = 2,
        GiftSabotage = 3,
        RevengeClue = 4
    }

    public class MissionGraphNodeData
    {
        public int NodeId { get; private set; }
        public short JobTitle { get; private set; }
        public string NodeKey { get; private set; }
        public MissionGraphNodeKind NodeKind { get; private set; }
        public int AreaType { get; private set; }
        public int ObjectType { get; private set; }
        public int InteractId { get; private set; }
        public List<int> RequiredPartIds { get; private set; }
        public List<int> RequiredItemIds { get; private set; }
        public List<int> RequiredNodeIds { get; private set; }
        public bool RequiresTargetLost { get; private set; }
        public int OutputPartId { get; private set; }
        public int StaminaCost { get; private set; }
        public int CorruptionDelta { get; private set; }
        public List<int> UnlockNodeIds { get; private set; }
        public string SuspicionTag { get; private set; }
        public LocalizedText Title { get; private set; }
        public LocalizedText AlibiClaim { get; private set; }
        public LocalizedText VisibleTrace { get; private set; }
        public LocalizedText SuccessText { get; private set; }

        public static MissionGraphNodeData CreateFromData(CsvRow row)
        {
            return new MissionGraphNodeData
            {
                NodeId = ParseInt(row, "node_id"),
                JobTitle = (short)ParseInt(row, "job_title"),
                NodeKey = ParseString(row, "node_key"),
                NodeKind = (MissionGraphNodeKind)ParseInt(row, "node_kind"),
                AreaType = ParseInt(row, "area_type"),
                ObjectType = ParseInt(row, "object_type"),
                InteractId = ParseInt(row, "interact_id"),
                RequiredPartIds = ParseIntList(row, "required_part_ids"),
                RequiredItemIds = ParseIntList(row, "required_item_ids"),
                RequiredNodeIds = ParseIntList(row, "required_node_ids"),
                RequiresTargetLost = ParseBool(row, "requires_target_lost"),
                OutputPartId = ParseInt(row, "output_part_id"),
                StaminaCost = ParseInt(row, "stamina_cost"),
                CorruptionDelta = ParseInt(row, "corruption_delta"),
                UnlockNodeIds = ParseIntList(row, "unlock_node_ids"),
                SuspicionTag = ParseString(row, "suspicion_tag"),
                Title = LocalizedText.FromCsv(row, "title"),
                AlibiClaim = LocalizedText.FromCsv(row, "alibi_claim"),
                VisibleTrace = LocalizedText.FromCsv(row, "visible_trace"),
                SuccessText = LocalizedText.FromCsv(row, "success_text")
            };
        }

        public bool IsInitiallyAvailable() =>
            RequiredPartIds.Count == 0 &&
            RequiredItemIds.Count == 0 &&
            RequiredNodeIds.Count == 0 &&
            !RequiresTargetLost;

        public bool AreRequirementsMet(
            HashSet<int> collectedPartIds,
            HashSet<int> completedNodeIds,
            bool hasLostTarget = false) =>
            RequiredPartIds.All(collectedPartIds.Contains) &&
            RequiredNodeIds.All(completedNodeIds.Contains) &&
            (!RequiresTargetLost || hasLostTarget);

        public bool MatchesInteractable(int areaType, int objectType, int interactId)
        {
            if (InteractId > 0)
                return interactId == InteractId;

            if (AreaType > 0 && areaType != AreaType)
                return false;

            if (ObjectType > 0 && objectType != ObjectType)
                return false;

            return AreaType > 0 || ObjectType > 0;
        }

        private static int ParseInt(CsvRow row, string columnName)
        {
            if (!row.ContainsKey(columnName) || string.IsNullOrWhiteSpace(row[columnName]))
                return 0;

            return int.Parse(row[columnName]);
        }

        private static string ParseString(CsvRow row, string columnName) =>
            row.ContainsKey(columnName) ? row[columnName] : "";

        private static bool ParseBool(CsvRow row, string columnName)
        {
            if (!row.ContainsKey(columnName) || string.IsNullOrWhiteSpace(row[columnName]))
                return false;

            var value = row[columnName].Trim();
            return value == "1" || value.Equals("true", System.StringComparison.OrdinalIgnoreCase);
        }

        private static List<int> ParseIntList(CsvRow row, string columnName)
        {
            if (!row.ContainsKey(columnName) || string.IsNullOrWhiteSpace(row[columnName]))
                return new List<int>();

            var value = row[columnName];
            if (value == "[]")
                return new List<int>();

            return JsonConvert.DeserializeObject<List<int>>(value) ?? new List<int>();
        }
    }

    public class MissionGraphRecipeData
    {
        public int RecipeId { get; private set; }
        public short JobTitle { get; private set; }
        public string RecipeKey { get; private set; }
        public List<int> InputPartIds { get; private set; }
        public int OutputPartId { get; private set; }
        public List<int> RequiredNodeIds { get; private set; }
        public List<int> UnlockNodeIds { get; private set; }
        public int CraftDurationSeconds { get; private set; }
        public int StaminaCost { get; private set; }
        public string SuspicionTag { get; private set; }
        public LocalizedText Title { get; private set; }
        public LocalizedText AlibiClaim { get; private set; }
        public LocalizedText VisibleTrace { get; private set; }
        public LocalizedText SuccessText { get; private set; }

        public static MissionGraphRecipeData CreateFromData(CsvRow row)
        {
            return new MissionGraphRecipeData
            {
                RecipeId = ParseInt(row, "recipe_id"),
                JobTitle = (short)ParseInt(row, "job_title"),
                RecipeKey = ParseString(row, "recipe_key"),
                InputPartIds = ParseIntList(row, "input_part_ids"),
                OutputPartId = ParseInt(row, "output_part_id"),
                RequiredNodeIds = ParseIntList(row, "required_node_ids"),
                UnlockNodeIds = ParseIntList(row, "unlock_node_ids"),
                CraftDurationSeconds = ParseInt(row, "craft_duration_seconds"),
                StaminaCost = ParseInt(row, "stamina_cost"),
                SuspicionTag = ParseString(row, "suspicion_tag"),
                Title = LocalizedText.FromCsv(row, "title"),
                AlibiClaim = LocalizedText.FromCsv(row, "alibi_claim"),
                VisibleTrace = LocalizedText.FromCsv(row, "visible_trace"),
                SuccessText = LocalizedText.FromCsv(row, "success_text")
            };
        }

        private static int ParseInt(CsvRow row, string columnName)
        {
            if (!row.ContainsKey(columnName) || string.IsNullOrWhiteSpace(row[columnName]))
                return 0;

            return int.Parse(row[columnName]);
        }

        private static string ParseString(CsvRow row, string columnName) =>
            row.ContainsKey(columnName) ? row[columnName] : "";

        private static List<int> ParseIntList(CsvRow row, string columnName)
        {
            if (!row.ContainsKey(columnName) || string.IsNullOrWhiteSpace(row[columnName]))
                return new List<int>();

            var value = row[columnName];
            if (value == "[]")
                return new List<int>();

            return JsonConvert.DeserializeObject<List<int>>(value) ?? new List<int>();
        }
    }
}
