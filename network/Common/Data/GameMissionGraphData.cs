// ReSharper disable All
#pragma warning disable CS8618
#pragma warning disable CS8603

using System;
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
        private static readonly List<MissionStoryletStartData> _storyletStarts = new();
        private static readonly List<MissionStoryletPoolData> _storyletPoolItems = new();

        public static void Initialize(List<CsvRow> nodeData, List<CsvRow> recipeData) =>
            Initialize(
                nodeData,
                recipeData,
                new List<CsvRow>(),
                new List<CsvRow>());

        public static void Initialize(
            List<CsvRow> nodeData,
            List<CsvRow> recipeData,
            List<CsvRow> storyletStartData,
            List<CsvRow> storyletPoolData)
        {
            _nodesById.Clear();
            _nodesByJob.Clear();
            _recipesById.Clear();
            _recipesByJob.Clear();
            _storyletStarts.Clear();
            _storyletPoolItems.Clear();

            _storyletStarts.AddRange((storyletStartData ?? new List<CsvRow>())
                .Select(MissionStoryletStartData.CreateFromData));
            _storyletPoolItems.AddRange((storyletPoolData ?? new List<CsvRow>())
                .Select(MissionStoryletPoolData.CreateFromData));

            var generatedStoryletNodes = GenerateStoryletPoolNodeRows(_storyletStarts, _storyletPoolItems);

            foreach (var row in (nodeData ?? new List<CsvRow>()).Concat(generatedStoryletNodes))
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
            MergeSharedAndJobLists(_nodesByJob, jobTitle);

        public static List<MissionGraphRecipeData> GetRecipes(short jobTitle) =>
            MergeSharedAndJobLists(_recipesByJob, jobTitle);

        public static List<MissionGraphNodeData> GetAllNodes() =>
            _nodesById.Values.ToList();

        public static List<MissionGraphRecipeData> GetAllRecipes() =>
            _recipesById.Values.ToList();

        public static List<MissionStoryletStartData> GetStoryletStarts() =>
            _storyletStarts.ToList();

        public static List<MissionStoryletPoolData> GetStoryletPoolItems() =>
            _storyletPoolItems.ToList();

        public static List<MissionGraphNodeData> GetInitiallyAvailableNodes(short jobTitle) =>
            GetNodes(jobTitle).Where(node => node.IsInitiallyAvailable()).ToList();

        public static List<MissionGraphNodeData> GetAvailableNodes(
            short jobTitle,
            IEnumerable<int> collectedPartIds,
            IEnumerable<int> completedNodeIds,
            bool hasLostTarget = false)
        {
            return GetAvailableNodes(
                jobTitle,
                collectedPartIds,
                completedNodeIds,
                Enumerable.Empty<string>(),
                hasLostTarget);
        }

        public static List<MissionGraphNodeData> GetAvailableNodes(
            short jobTitle,
            IEnumerable<int> collectedPartIds,
            IEnumerable<int> completedNodeIds,
            IEnumerable<string> ownedClueTags,
            bool hasLostTarget = false)
        {
            var collectedPartSet = new HashSet<int>(collectedPartIds ?? Enumerable.Empty<int>());
            var completedNodeSet = new HashSet<int>(completedNodeIds ?? Enumerable.Empty<int>());
            var ownedClueTagSet = new HashSet<string>(
                ownedClueTags ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            return GetNodes(jobTitle)
                .Where(node => node.AreRequirementsMet(
                    collectedPartSet,
                    completedNodeSet,
                    ownedClueTagSet,
                    hasLostTarget))
                .ToList();
        }

        public static bool HasMissionActionTarget(int areaType, int objectType, int interactId)
        {
            return _nodesById.Values.Any(node =>
                node.NodeKind != MissionGraphNodeKind.CollectPart &&
                node.MatchesInteractable(areaType, objectType, interactId));
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

        private static List<T> MergeSharedAndJobLists<T>(Dictionary<short, List<T>> source, short jobTitle)
        {
            if (jobTitle == 0)
                return source.TryGetValue(0, out var sharedOnlyList) ? sharedOnlyList.ToList() : new List<T>();

            var merged = new List<T>();
            if (source.TryGetValue(0, out var sharedList))
                merged.AddRange(sharedList);
            if (source.TryGetValue(jobTitle, out var jobList))
                merged.AddRange(jobList);

            return merged;
        }

        private static List<CsvRow> GenerateStoryletPoolNodeRows(
            List<MissionStoryletStartData> starts,
            List<MissionStoryletPoolData> poolItems)
        {
            var rows = new List<CsvRow>();
            if (starts.Count == 0)
                return rows;

            foreach (var start in starts.OrderBy(data => data.StartIndex))
            {
                rows.Add(CreateStoryletNodeRow(
                    start.NodeId,
                    $"START-{start.StartKey}",
                    MissionGraphStoryletType.Discovery,
                    start.TargetAreaType,
                    start.TargetObjectType,
                    start.InteractId,
                    start.ActionGroupKey,
                    start.RequiredPartIds,
                    new List<int>(),
                    new List<int>(),
                    start.RequiredOutputItemId,
                    start.RecipeId,
                    RenderStartClaimKey(start),
                    start.ClaimPolicy,
                    start.RewardKind,
                    start.CaseGroup,
                    "",
                    "",
                    "",
                    JoinTags(
                        "record_case",
                        $"start_{NormalizeKey(start.StartKey)}",
                        "stage_1"),
                    "",
                    "",
                    start.Title,
                    start.SuccessText,
                    false));
            }

            foreach (var item in poolItems.OrderBy(data => data.StageIndex).ThenBy(data => data.NodeId))
            {
                rows.Add(CreateStoryletNodeRow(
                    item.NodeId,
                    string.IsNullOrWhiteSpace(item.NodeKey)
                        ? $"{item.StageKey}-{item.PoolKey}"
                        : item.NodeKey,
                    item.IsVictoryStorylet ? MissionGraphStoryletType.Victory : item.StoryletType,
                    item.TargetAreaType,
                    item.TargetObjectType,
                    item.InteractId,
                    item.ActionGroupKey,
                    new List<int>(),
                    new List<int>(),
                    new List<int>(),
                    0,
                    0,
                    RenderPoolClaimKey(item),
                    item.ClaimPolicy,
                    item.RewardKind,
                    item.CaseGroup,
                    JoinTags(item.RequiredAllTags.ToArray()),
                    JoinTags(item.RequiredAnyTags.ToArray()),
                    JoinTags(item.BlockedTags.ToArray()),
                    JoinTags(item.GrantTags.ToArray()),
                    JoinTags(item.FinalTags.ToArray()),
                    item.SuspicionTag,
                    item.Title,
                    item.SuccessText,
                    item.IsVictoryStorylet));
            }

            return rows;
        }

        // claim key는 case_group/key에서 완전 파생되므로 CSV의 claim_key_template 컬럼 없이 고정 패턴으로 생성한다.
        private static string RenderStartClaimKey(MissionStoryletStartData start)
        {
            return "record/start/{start_key}"
                .Replace("{case_group}", start.CaseGroup)
                .Replace("{start_key}", start.StartKey)
                .Replace("{stage_index}", "1");
        }

        private static string RenderPoolClaimKey(MissionStoryletPoolData item)
        {
            return "record/pool/{pool_key}"
                .Replace("{case_group}", item.CaseGroup)
                .Replace("{pool_key}", item.PoolKey)
                .Replace("{stage_key}", item.StageKey)
                .Replace("{stage_index}", item.StageIndex.ToString());
        }

        private static string NormalizeKey(string value) =>
            string.IsNullOrWhiteSpace(value) ? "" : value.ToLowerInvariant();

        private static string JoinTags(params string[] tags) =>
            string.Join("|", tags.Where(tag => !string.IsNullOrWhiteSpace(tag)));

        private static CsvRow CreateStoryletNodeRow(
            int nodeId,
            string nodeKey,
            MissionGraphStoryletType storyletType,
            int targetAreaType,
            int targetObjectType,
            int interactId,
            string actionGroupKey,
            List<int> requiredPartIds,
            List<int> requiredNodeIds,
            List<int> unlockNodeIds,
            int requiredOutputItemId,
            int recipeId,
            string storyletId,
            MissionGraphClaimPolicy claimPolicy,
            MissionGraphRewardKind rewardKind,
            string caseGroup,
            string requiredAllTags,
            string requiredAnyTags,
            string blockedTags,
            string clueTags,
            string finalTags,
            string suspicionTag,
            LocalizedText title,
            LocalizedText successText,
            bool isVictoryStorylet)
        {
            string[] values =
            {
                nodeId.ToString(),
                "0",
                nodeKey,
                ((int)MissionGraphNodeKind.UseFeature).ToString(),
                targetAreaType.ToString(),
                targetObjectType.ToString(),
                interactId.ToString(),
                ToJsonIntList(requiredPartIds),
                "[]",
                ToJsonIntList(requiredNodeIds),
                "false",
                "0",
                "0",
                "0",
                ToJsonIntList(unlockNodeIds),
                suspicionTag,
                title.Kr,
                title.En,
                title.Jp,
                successText.Kr,
                successText.En,
                successText.Jp,
                successText.Kr,
                successText.En,
                successText.Jp,
                storyletId,
                ToCsvToken(storyletType),
                targetAreaType.ToString(),
                targetObjectType.ToString(),
                actionGroupKey,
                requiredOutputItemId.ToString(),
                recipeId.ToString(),
                requiredAllTags,
                requiredAnyTags,
                blockedTags,
                clueTags,
                finalTags,
                caseGroup,
                "",
                "0",
                ToCsvToken(claimPolicy),
                ToCsvToken(rewardKind),
                "0",
                "0",
                "0",
                isVictoryStorylet ? "true" : "false"
            };

            return new CsvRow(NodeCsvHeaders, values);
        }

        private static string ToJsonIntList(List<int> values) =>
            values == null || values.Count == 0 ? "[]" : JsonConvert.SerializeObject(values);

        private static string ToCsvToken<TEnum>(TEnum value)
            where TEnum : struct, Enum
        {
            var name = value.ToString();
            return string.Concat(name.Select((c, index) =>
                    index > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
        }

        private static readonly string[] NodeCsvHeaders =
        {
            "node_id",
            "job_title",
            "node_key",
            "node_kind",
            "area_type",
            "object_type",
            "interact_id",
            "required_part_ids",
            "required_item_ids",
            "required_node_ids",
            "requires_target_lost",
            "output_part_id",
            "stamina_cost",
            "corruption_delta",
            "unlock_node_ids",
            "suspicion_tag",
            "title_kr",
            "title_en",
            "title_jp",
            "visible_trace_kr",
            "visible_trace_en",
            "visible_trace_jp",
            "success_text_kr",
            "success_text_en",
            "success_text_jp",
            "storylet_id",
            "storylet_type",
            "target_area_type",
            "target_object_type",
            "action_group_key",
            "required_output_item_id",
            "recipe_id",
            "required_all_tags",
            "required_any_tags",
            "blocked_tags",
            "clue_tags",
            "final_tags",
            "case_group",
            "stat_check",
            "stat_threshold",
            "claim_policy",
            "reward_kind",
            "location_hint_text_id",
            "trace_text_id",
            "contested_text_id",
            "is_victory_storylet"
        };
    }

    public enum MissionGraphNodeKind
    {
        CollectPart = 1,
        UseFeature = 2,
        GiftSabotage = 3,
        RevengeClue = 4
    }

    public enum MissionGraphStoryletType
    {
        None = 0,
        Discovery = 1,
        Route = 2,
        Victory = 3
    }

    public enum MissionGraphClaimPolicy
    {
        None = 0,
        Unique = 1,
        Shared = 2,
        Repeatable = 3
    }

    public enum MissionGraphRewardKind
    {
        None = 0,
        Stat = 1,
        Consumable = 2,
        ClueTag = 3,
        OutputItem = 4,
        Mixed = 5
    }

    public class MissionStoryletStartData
    {
        public string CaseGroup { get; private set; }
        public string StartKey { get; private set; }
        public int StartIndex { get; private set; }
        public int NodeId { get; private set; }
        public LocalizedText Title { get; private set; }
        public List<int> RequiredPartIds { get; private set; }
        public int RequiredOutputItemId { get; private set; }
        public int RecipeId { get; private set; }
        public int TargetAreaType { get; private set; }
        public int TargetObjectType { get; private set; }
        public int InteractId { get; private set; }
        public string ActionGroupKey { get; private set; }
        public MissionGraphClaimPolicy ClaimPolicy { get; private set; }        public MissionGraphRewardKind RewardKind { get; private set; }
        public LocalizedText SuccessText { get; private set; }

        public static MissionStoryletStartData CreateFromData(CsvRow row)
        {
            int startIndex = MissionStoryletCsv.ParseInt(row, "start_index");
            return new MissionStoryletStartData
            {
                CaseGroup = MissionStoryletCsv.ParseString(row, "case_group"),
                StartKey = MissionStoryletCsv.ParseString(row, "start_key"),
                StartIndex = startIndex,
                NodeId = MissionStoryletCsv.ParseInt(row, "node_id", 4100 + startIndex),
                Title = LocalizedText.FromCsv(row, "title"),
                RequiredPartIds = MissionStoryletCsv.ParseIntList(row, "required_part_ids"),
                RequiredOutputItemId = MissionStoryletCsv.ParseInt(row, "required_output_item_id"),
                RecipeId = MissionStoryletCsv.ParseInt(row, "recipe_id"),
                TargetAreaType = MissionStoryletCsv.ParseInt(row, "target_area_type"),
                TargetObjectType = MissionStoryletCsv.ParseInt(row, "target_object_type"),
                InteractId = MissionStoryletCsv.ParseInt(row, "interact_id"),
                ActionGroupKey = MissionStoryletCsv.ParseString(row, "action_group_key"),
                ClaimPolicy = MissionStoryletCsv.ParseEnum(row, "claim_policy", MissionGraphClaimPolicy.Unique),                RewardKind = MissionStoryletCsv.ParseEnum(row, "reward_kind", MissionGraphRewardKind.ClueTag),
                SuccessText = LocalizedText.FromCsvMultiline(row, "success_text")
            };
        }
    }

    public class MissionStoryletPoolData
    {
        public string CaseGroup { get; private set; }
        public int NodeId { get; private set; }
        public string PoolKey { get; private set; }
        public string NodeKey { get; private set; }
        public int StageIndex { get; private set; }
        public string StageKey { get; private set; }
        public LocalizedText Title { get; private set; }
        public int TargetAreaType { get; private set; }
        public int TargetObjectType { get; private set; }
        public int InteractId { get; private set; }
        public string ActionGroupKey { get; private set; }
        public MissionGraphStoryletType StoryletType { get; private set; }
        public MissionGraphClaimPolicy ClaimPolicy { get; private set; }        public MissionGraphRewardKind RewardKind { get; private set; }
        public string SuspicionTag { get; private set; }
        public bool IsVictoryStorylet { get; private set; }
        public List<string> RequiredAllTags { get; private set; }
        public List<string> RequiredAnyTags { get; private set; }
        public List<string> BlockedTags { get; private set; }
        public List<string> GrantTags { get; private set; }
        public List<string> FinalTags { get; private set; }
        public LocalizedText SuccessText { get; private set; }

        public static MissionStoryletPoolData CreateFromData(CsvRow row)
        {
            var storyletType = MissionStoryletCsv.ParseEnum(row, "storylet_type", MissionGraphStoryletType.Route);
            bool isVictoryStorylet = MissionStoryletCsv.ParseBool(row, "is_victory_storylet") ||
                                      storyletType == MissionGraphStoryletType.Victory;

            if (isVictoryStorylet && storyletType == MissionGraphStoryletType.None)
                storyletType = MissionGraphStoryletType.Victory;

            return new MissionStoryletPoolData
            {
                CaseGroup = MissionStoryletCsv.ParseString(row, "case_group"),
                NodeId = MissionStoryletCsv.ParseInt(row, "node_id"),
                PoolKey = MissionStoryletCsv.ParseString(row, "pool_key"),
                NodeKey = MissionStoryletCsv.ParseString(row, "node_key"),
                StageIndex = MissionStoryletCsv.ParseInt(row, "stage_index"),
                StageKey = MissionStoryletCsv.ParseString(row, "stage_key"),
                Title = LocalizedText.FromCsv(row, "title"),
                TargetAreaType = MissionStoryletCsv.ParseInt(row, "target_area_type"),
                TargetObjectType = MissionStoryletCsv.ParseInt(row, "target_object_type"),
                InteractId = MissionStoryletCsv.ParseInt(row, "interact_id"),
                ActionGroupKey = MissionStoryletCsv.ParseString(row, "action_group_key"),
                StoryletType = storyletType,
                ClaimPolicy = MissionStoryletCsv.ParseEnum(row, "claim_policy", MissionGraphClaimPolicy.Unique),
                RewardKind = MissionStoryletCsv.ParseEnum(row, "reward_kind", MissionGraphRewardKind.ClueTag),
                SuspicionTag = MissionStoryletCsv.ParseString(row, "suspicion_tag"),
                IsVictoryStorylet = isVictoryStorylet,
                RequiredAllTags = MissionStoryletCsv.ParseStringList(row, "required_all_tags"),
                RequiredAnyTags = MissionStoryletCsv.ParseStringList(row, "required_any_tags"),
                BlockedTags = MissionStoryletCsv.ParseStringList(row, "blocked_tags"),
                GrantTags = MissionStoryletCsv.ParseStringList(row, "grant_tags"),
                FinalTags = MissionStoryletCsv.ParseStringList(row, "final_tags"),
                SuccessText = LocalizedText.FromCsvMultiline(row, "success_text")
            };
        }
    }

    internal static class MissionStoryletCsv
    {
        public static bool HasValue(CsvRow row, string columnName) =>
            row.ContainsKey(columnName) && !string.IsNullOrWhiteSpace(row[columnName]);

        public static int ParseInt(CsvRow row, string columnName, int defaultValue = 0)
        {
            if (!HasValue(row, columnName))
                return defaultValue;

            return int.Parse(row[columnName]);
        }

        public static string ParseString(CsvRow row, string columnName) =>
            row.ContainsKey(columnName) ? row[columnName] : "";

        public static bool ParseBool(CsvRow row, string columnName)
        {
            if (!HasValue(row, columnName))
                return false;

            var value = row[columnName].Trim();
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public static List<int> ParseIntList(CsvRow row, string columnName)
        {
            if (!HasValue(row, columnName) || row[columnName] == "[]")
                return new List<int>();

            return JsonConvert.DeserializeObject<List<int>>(row[columnName]) ?? new List<int>();
        }

        public static List<string> ParseStringList(CsvRow row, string columnName)
        {
            if (!HasValue(row, columnName) || row[columnName] == "[]")
                return new List<string>();

            var value = row[columnName].Trim();
            if (value.StartsWith("["))
            {
                try
                {
                    return JsonConvert.DeserializeObject<List<string>>(value) ?? new List<string>();
                }
                catch (JsonException)
                {
                    value = value.Trim('[', ']');
                    return value.Split(',', '|')
                        .Select(item => item.Trim().Trim('"'))
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .ToList();
                }
            }

            return value.Split('|')
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        public static TEnum ParseEnum<TEnum>(CsvRow row, string columnName, TEnum defaultValue)
            where TEnum : struct, Enum
        {
            if (!HasValue(row, columnName))
                return defaultValue;

            var value = row[columnName].Trim();
            if (int.TryParse(value, out int intValue) && Enum.IsDefined(typeof(TEnum), intValue))
                return (TEnum)Enum.ToObject(typeof(TEnum), intValue);

            var normalizedValue = NormalizeEnumToken(value);
            foreach (var name in Enum.GetNames(typeof(TEnum)))
            {
                if (NormalizeEnumToken(name) == normalizedValue)
                    return Enum.Parse<TEnum>(name);
            }

            return defaultValue;
        }

        private static string NormalizeEnumToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return value
                .Replace("_", "")
                .Replace("-", "")
                .Replace(" ", "")
                .ToLowerInvariant();
        }
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
        public LocalizedText VisibleTrace { get; private set; }
        public LocalizedText SuccessText { get; private set; }
        public string StoryletId { get; private set; }
        public MissionGraphStoryletType StoryletType { get; private set; }
        public int TargetAreaType { get; private set; }
        public int TargetObjectType { get; private set; }
        public string ActionGroupKey { get; private set; }
        public int RequiredOutputItemId { get; private set; }
        public int RecipeId { get; private set; }
        public List<string> RequiredAllTags { get; private set; }
        public List<string> RequiredAnyTags { get; private set; }
        public List<string> BlockedTags { get; private set; }
        public List<string> ClueTags { get; private set; }
        public List<string> FinalTags { get; private set; }
        public string CaseGroup { get; private set; }
        public string StatCheck { get; private set; }
        public int StatThreshold { get; private set; }
        public MissionGraphClaimPolicy ClaimPolicy { get; private set; }
        public MissionGraphRewardKind RewardKind { get; private set; }
        public int LocationHintTextId { get; private set; }
        public int TraceTextId { get; private set; }
        public int ContestedTextId { get; private set; }
        public bool IsVictoryStorylet { get; private set; }
        public string EffectiveStoryletId => !string.IsNullOrWhiteSpace(StoryletId) ? StoryletId : NodeKey;

        public bool HasStoryletMetadata =>
            !string.IsNullOrWhiteSpace(StoryletId) ||
            StoryletType != MissionGraphStoryletType.None ||
            ClaimPolicy != MissionGraphClaimPolicy.None ||
            IsVictoryStorylet;

        public static MissionGraphNodeData CreateFromData(CsvRow row)
        {
            var nodeKey = ParseString(row, "node_key");
            var storyletType = ParseEnum(row, "storylet_type", MissionGraphStoryletType.None);
            bool isVictoryStorylet = ParseBool(row, "is_victory_storylet") ||
                                      storyletType == MissionGraphStoryletType.Victory;

            if (isVictoryStorylet && storyletType == MissionGraphStoryletType.None)
                storyletType = MissionGraphStoryletType.Victory;

            return new MissionGraphNodeData
            {
                NodeId = ParseInt(row, "node_id"),
                JobTitle = (short)ParseInt(row, "job_title"),
                NodeKey = nodeKey,
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
                VisibleTrace = LocalizedText.FromCsvMultiline(row, "visible_trace"),
                SuccessText = LocalizedText.FromCsvMultiline(row, "success_text"),
                StoryletId = ParseString(row, "storylet_id"),
                StoryletType = storyletType,
                TargetAreaType = ParseInt(row, "target_area_type"),
                TargetObjectType = ParseInt(row, "target_object_type"),
                ActionGroupKey = ParseString(row, "action_group_key"),
                RequiredOutputItemId = ParseInt(row, "required_output_item_id"),
                RecipeId = ParseInt(row, "recipe_id"),
                RequiredAllTags = ParseStringList(row, "required_all_tags"),
                RequiredAnyTags = ParseStringList(row, "required_any_tags"),
                BlockedTags = ParseStringList(row, "blocked_tags"),
                ClueTags = ParseStringList(row, "clue_tags"),
                FinalTags = ParseStringList(row, "final_tags"),
                CaseGroup = ParseString(row, "case_group"),
                StatCheck = ParseString(row, "stat_check"),
                StatThreshold = ParseInt(row, "stat_threshold"),
                ClaimPolicy = ParseEnum(row, "claim_policy", MissionGraphClaimPolicy.None),
                RewardKind = ParseEnum(row, "reward_kind", MissionGraphRewardKind.None),
                LocationHintTextId = ParseInt(row, "location_hint_text_id"),
                TraceTextId = ParseInt(row, "trace_text_id"),
                ContestedTextId = ParseInt(row, "contested_text_id"),
                IsVictoryStorylet = isVictoryStorylet
            };
        }

        public bool IsInitiallyAvailable() =>
            RequiredPartIds.Count == 0 &&
            RequiredItemIds.Count == 0 &&
            RequiredNodeIds.Count == 0 &&
            RequiredAllTags.Count == 0 &&
            RequiredAnyTags.Count == 0 &&
            !RequiresTargetLost;

        public bool AreRequirementsMet(
            HashSet<int> collectedPartIds,
            HashSet<int> completedNodeIds,
            bool hasLostTarget = false) =>
            AreRequirementsMet(
                collectedPartIds,
                completedNodeIds,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                hasLostTarget);

        public bool AreRequirementsMet(
            HashSet<int> collectedPartIds,
            HashSet<int> completedNodeIds,
            HashSet<string> ownedClueTags,
            bool hasLostTarget = false) =>
            RequiredPartIds.All(collectedPartIds.Contains) &&
            (RequiredOutputItemId <= 0 || collectedPartIds.Contains(RequiredOutputItemId)) &&
            RequiredNodeIds.All(completedNodeIds.Contains) &&
            RequiredAllTags.All(ownedClueTags.Contains) &&
            (RequiredAnyTags.Count == 0 || RequiredAnyTags.Any(ownedClueTags.Contains)) &&
            !BlockedTags.Any(ownedClueTags.Contains) &&
            (!RequiresTargetLost || hasLostTarget);

        public bool MatchesInteractable(int areaType, int objectType, int interactId)
        {
            if (InteractId > 0)
                return interactId == InteractId;

            int expectedAreaType = TargetAreaType > 0 ? TargetAreaType : AreaType;
            int expectedObjectType = TargetObjectType > 0 ? TargetObjectType : ObjectType;

            if (expectedAreaType > 0 && areaType != expectedAreaType)
                return false;

            if (expectedObjectType > 0 && objectType != expectedObjectType)
                return false;

            return expectedAreaType > 0 || expectedObjectType > 0;
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

        private static TEnum ParseEnum<TEnum>(CsvRow row, string columnName, TEnum defaultValue)
            where TEnum : struct, Enum
        {
            if (!row.ContainsKey(columnName) || string.IsNullOrWhiteSpace(row[columnName]))
                return defaultValue;

            var value = row[columnName].Trim();
            if (int.TryParse(value, out int intValue) && Enum.IsDefined(typeof(TEnum), intValue))
                return (TEnum)Enum.ToObject(typeof(TEnum), intValue);

            var normalizedValue = NormalizeEnumToken(value);
            foreach (var name in Enum.GetNames(typeof(TEnum)))
            {
                if (NormalizeEnumToken(name) == normalizedValue)
                    return Enum.Parse<TEnum>(name);
            }

            return defaultValue;
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

        private static List<string> ParseStringList(CsvRow row, string columnName)
        {
            if (!row.ContainsKey(columnName) || string.IsNullOrWhiteSpace(row[columnName]))
                return new List<string>();

            var value = row[columnName].Trim();
            if (value == "[]")
                return new List<string>();

            if (value.StartsWith("["))
            {
                try
                {
                    return JsonConvert.DeserializeObject<List<string>>(value) ?? new List<string>();
                }
                catch (JsonException)
                {
                    value = value.Trim('[', ']');
                    return value.Split(',', '|')
                        .Select(item => item.Trim().Trim('"'))
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .ToList();
                }
            }

            return value.Split('|')
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        private static string NormalizeEnumToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return value
                .Replace("_", "")
                .Replace("-", "")
                .Replace(" ", "")
                .ToLowerInvariant();
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
                AlibiClaim = LocalizedText.FromCsvMultiline(row, "alibi_claim"),
                VisibleTrace = LocalizedText.FromCsvMultiline(row, "visible_trace"),
                SuccessText = LocalizedText.FromCsvMultiline(row, "success_text")
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
