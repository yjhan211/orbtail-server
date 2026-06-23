// ReSharper disable All
#pragma warning disable CS8618
#pragma warning disable CS8603

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using network.common;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GameChecklistData
    {
        private static readonly Dictionary<string, ChecklistRuleData> _rules =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<int, ChecklistTaskData> _tasksById = new();
        private static readonly Dictionary<string, ChecklistTaskData> _tasksByKey =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<ChecklistTaskCategory, List<ChecklistTaskData>> _tasksByCategory = new();
        private static readonly Dictionary<ChecklistChainState, ChecklistStateRuleData> _stateRules = new();

        public static void Initialize(
            List<CsvRow> ruleData,
            List<CsvRow> taskPoolData,
            List<CsvRow> stateRuleData)
        {
            _rules.Clear();
            _tasksById.Clear();
            _tasksByKey.Clear();
            _tasksByCategory.Clear();
            _stateRules.Clear();

            foreach (var row in ruleData)
            {
                var rule = ChecklistRuleData.CreateFromData(row);
                _rules[rule.Key] = rule;
            }

            foreach (var row in taskPoolData)
            {
                var task = ChecklistTaskData.CreateFromData(row);
                _tasksById[task.TaskId] = task;
                _tasksByKey[task.TaskKey] = task;

                if (!_tasksByCategory.TryGetValue(task.Category, out var categoryTasks))
                {
                    categoryTasks = new List<ChecklistTaskData>();
                    _tasksByCategory[task.Category] = categoryTasks;
                }

                categoryTasks.Add(task);
            }

            foreach (var tasks in _tasksByCategory.Values)
                tasks.Sort((a, b) => a.TaskId.CompareTo(b.TaskId));

            foreach (var row in stateRuleData)
            {
                var stateRule = ChecklistStateRuleData.CreateFromData(row);
                _stateRules[stateRule.ChainState] = stateRule;
            }
        }

        public static ChecklistRuleData GetRule(string key) =>
            _rules.TryGetValue(key, out var rule) ? rule : null;

        public static string GetRuleValue(string key, string fallback = "") =>
            GetRule(key)?.Value ?? fallback;

        public static int GetRuleInt(string key, int fallback = 0) =>
            int.TryParse(GetRuleValue(key), out var value) ? value : fallback;

        public static float GetRuleFloat(string key, float fallback = 0f) =>
            float.TryParse(GetRuleValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;

        public static bool GetRuleBool(string key, bool fallback = false)
        {
            var value = GetRuleValue(key);
            return string.IsNullOrEmpty(value) ? fallback : ChecklistCsvParser.ParseBool(value);
        }

        public static ChecklistTaskData GetTask(int taskId) =>
            _tasksById.TryGetValue(taskId, out var task) ? task : null;

        public static ChecklistTaskData GetTask(string taskKey) =>
            _tasksByKey.TryGetValue(taskKey, out var task) ? task : null;

        public static List<ChecklistTaskData> GetAllTasks() =>
            _tasksById.Values.OrderBy(task => task.TaskId).ToList();

        public static List<ChecklistTaskData> GetTasks(ChecklistTaskCategory category) =>
            _tasksByCategory.TryGetValue(category, out var tasks)
                ? tasks.ToList()
                : new List<ChecklistTaskData>();

        public static List<ChecklistTaskData> GetGeneralJobs() =>
            GetTasks(ChecklistTaskCategory.GeneralJob);

        public static List<ChecklistTaskData> GetManittoRoles() =>
            GetTasks(ChecklistTaskCategory.ManittoRole);

        public static ChecklistStateRuleData GetStateRule(ChecklistChainState chainState) =>
            _stateRules.TryGetValue(chainState, out var stateRule) ? stateRule : null;

        public static List<ChecklistStateRuleData> GetAllStateRules() =>
            _stateRules.Values.OrderBy(rule => rule.ChainState).ToList();

        public static void Validate()
        {
            var errors = new List<string>();

            if (_rules.Count == 0)
                errors.Add("checklist_rule.csv has no rows");

            if (_tasksById.Count == 0)
                errors.Add("checklist_task_pool.csv has no rows");

            if (_stateRules.Count == 0)
                errors.Add("checklist_state_rules.csv has no rows");

            foreach (ChecklistChainState state in Enum.GetValues(typeof(ChecklistChainState)))
            {
                if (state == ChecklistChainState.None)
                    continue;

                if (!_stateRules.ContainsKey(state))
                    errors.Add($"checklist_state_rules missing state={state}");
            }

            foreach (var task in _tasksById.Values)
            {
                if (task.Score <= 0)
                    errors.Add($"checklist_task_pool [{task.TaskId}]: score must be positive");

                if (task.StaminaCost < 0)
                    errors.Add($"checklist_task_pool [{task.TaskId}]: stamina_cost cannot be negative");

                if (task.Category == ChecklistTaskCategory.GeneralJob)
                {
                    if (task.DurationSeconds <= 0)
                        errors.Add($"checklist_task_pool [{task.TaskId}]: general_job duration_sec must be positive");

                    if (task.RequiredItemId <= 0)
                        errors.Add($"checklist_task_pool [{task.TaskId}]: general_job requires required_item_id");
                }
            }

            if (errors.Count > 0)
                throw new InvalidDataException($"Checklist data validation failed: {errors.Count}\n{string.Join("\n", errors)}");
        }

        public static void ValidateReferentialIntegrity(List<string> errors, HashSet<int> itemIds)
        {
            var interactables = GameInteractableData.GetAll();

            foreach (var task in _tasksById.Values)
            {
                if (task.AreaType > 0 && !Enum.IsDefined(typeof(AreaType), task.AreaType))
                    errors.Add($"checklist_task_pool [{task.TaskId}]: area_type={task.AreaType} invalid");

                if (task.ObjectType > 0 && !Enum.IsDefined(typeof(InteractableObjectType), task.ObjectType))
                    errors.Add($"checklist_task_pool [{task.TaskId}]: object_type={task.ObjectType} invalid");

                if (task.RequiredItemId > 0 && !itemIds.Contains(task.RequiredItemId))
                    errors.Add($"checklist_task_pool [{task.TaskId}]: required_item_id={task.RequiredItemId} not found in item_info");

                if (task.InteractId <= 0)
                    continue;

                var interactable = interactables.FirstOrDefault(info => info.Id == task.InteractId);
                if (interactable == null)
                {
                    errors.Add($"checklist_task_pool [{task.TaskId}]: interact_id={task.InteractId} not found");
                    continue;
                }

                if (task.AreaType > 0 && interactable.ZoneId != task.AreaType)
                    errors.Add(
                        $"checklist_task_pool [{task.TaskId}]: interact_id={task.InteractId} area mismatch ({interactable.ZoneId} != {task.AreaType})");

                if (task.ObjectType > 0 && (int)interactable.ObjectType != task.ObjectType)
                    errors.Add(
                        $"checklist_task_pool [{task.TaskId}]: interact_id={task.InteractId} object mismatch ({(int)interactable.ObjectType} != {task.ObjectType})");
            }
        }
    }

    public enum ChecklistTaskCategory
    {
        None = 0,
        GeneralJob = 1,
        ManittoRole = 2
    }

    public enum ChecklistRequiredItemPolicy
    {
        None = 0,
        Keep = 1,
        Consume = 2
    }

    public enum ChecklistChainState
    {
        None = 0,
        Normal = 1,
        TargetLost = 2,
        ManittoLost = 3,
        BothLost = 4
    }

    public class ChecklistRuleData
    {
        public string Key { get; private set; }
        public string Value { get; private set; }
        public string CommentKr { get; private set; }

        public static ChecklistRuleData CreateFromData(CsvRow row) =>
            new()
            {
                Key = row["rule_key"],
                Value = row["value"],
                CommentKr = row.ContainsKey("comment_kr") ? row["comment_kr"] : ""
            };
    }

    public class ChecklistTaskData
    {
        public int TaskId { get; private set; }
        public ChecklistTaskCategory Category { get; private set; }
        public string TaskKey { get; private set; }
        public string TitleKr { get; private set; }
        public string DescriptionKr { get; private set; }
        public float Score { get; private set; }
        public int AreaType { get; private set; }
        public string AreaNameKr { get; private set; }
        public int ObjectType { get; private set; }
        public int InteractId { get; private set; }
        public int StaminaCost { get; private set; }
        public int DurationSeconds { get; private set; }
        public int RequiredItemId { get; private set; }
        public string RequiredItemNameKr { get; private set; }
        public ChecklistRequiredItemPolicy RequiredItemPolicy { get; private set; }
        public bool ConsumeRequiredItem { get; private set; }
        public bool RequiresTargetAlive { get; private set; }
        public bool RequiresTargetLost { get; private set; }
        public bool RequiresManittoAlive { get; private set; }
        public bool RequiresGiftItem { get; private set; }
        public string CompletionEvent { get; private set; }
        public string ChainPolicy { get; private set; }
        public string RoundLimit { get; private set; }
        public List<string> AntiFarmTags { get; private set; }
        public string SuccessLogKr { get; private set; }

        public static ChecklistTaskData CreateFromData(CsvRow row)
        {
            return new ChecklistTaskData
            {
                TaskId = int.Parse(row["task_id"]),
                Category = ChecklistCsvParser.ParseCategory(row["category"]),
                TaskKey = row["task_key"],
                TitleKr = row["title_kr"],
                DescriptionKr = row["description_kr"],
                Score = float.Parse(row["score"], CultureInfo.InvariantCulture),
                AreaType = ChecklistCsvParser.ParseInt(row, "area_type"),
                AreaNameKr = row["area_name_kr"],
                ObjectType = ChecklistCsvParser.ParseInt(row, "object_type"),
                InteractId = ChecklistCsvParser.ParseInt(row, "interact_id"),
                StaminaCost = ChecklistCsvParser.ParseInt(row, "stamina_cost"),
                DurationSeconds = ChecklistCsvParser.ParseInt(row, "duration_sec"),
                RequiredItemId = ChecklistCsvParser.ParseInt(row, "required_item_id"),
                RequiredItemNameKr = row["required_item_name_kr"],
                RequiredItemPolicy = ChecklistCsvParser.ParseRequiredItemPolicy(row["required_item_policy"]),
                ConsumeRequiredItem = ChecklistCsvParser.ParseBool(row["consume_required_item"]),
                RequiresTargetAlive = ChecklistCsvParser.ParseBool(row["requires_target_alive"]),
                RequiresTargetLost = ChecklistCsvParser.ParseBool(row["requires_target_lost"]),
                RequiresManittoAlive = ChecklistCsvParser.ParseBool(row["requires_maniito_alive"]),
                RequiresGiftItem = ChecklistCsvParser.ParseBool(row["requires_gift_item"]),
                CompletionEvent = row["completion_event"],
                ChainPolicy = row["chain_policy"],
                RoundLimit = row["round_limit"],
                AntiFarmTags = ChecklistCsvParser.ParseTags(row["anti_farm_tags"]),
                SuccessLogKr = row["success_log_kr"]
            };
        }
    }

    public class ChecklistStateRuleData
    {
        public ChecklistChainState ChainState { get; private set; }
        public bool TargetAlive { get; private set; }
        public bool ManittoAlive { get; private set; }
        public int GeneralJobCount { get; private set; }
        public int ManittoRoleMinCount { get; private set; }
        public int ManittoRoleMaxCount { get; private set; }
        public bool CanAccuse { get; private set; }
        public string NotesKr { get; private set; }

        public static ChecklistStateRuleData CreateFromData(CsvRow row)
        {
            var (min, max) = ChecklistCsvParser.ParseCountRange(row["round_start_manitto_role_count"]);

            return new ChecklistStateRuleData
            {
                ChainState = ChecklistCsvParser.ParseChainState(row["chain_state"]),
                TargetAlive = ChecklistCsvParser.ParseBool(row["target_alive"]),
                ManittoAlive = ChecklistCsvParser.ParseBool(row["manitto_alive"]),
                GeneralJobCount = int.Parse(row["round_start_active_general_job_count"]),
                ManittoRoleMinCount = min,
                ManittoRoleMaxCount = max,
                CanAccuse = ChecklistCsvParser.ParseBool(row["can_accuse"]),
                NotesKr = row["notes_kr"]
            };
        }
    }

    internal static class ChecklistCsvParser
    {
        public static int ParseInt(CsvRow row, string columnName) =>
            row.ContainsKey(columnName) && int.TryParse(row[columnName], out var value) ? value : 0;

        public static bool ParseBool(string value)
        {
            var normalized = value.Trim();
            return normalized == "1" || normalized.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public static ChecklistTaskCategory ParseCategory(string value) =>
            value.Trim().ToLowerInvariant() switch
            {
                "general_job" => ChecklistTaskCategory.GeneralJob,
                "manitto_role" => ChecklistTaskCategory.ManittoRole,
                _ => ChecklistTaskCategory.None
            };

        public static ChecklistRequiredItemPolicy ParseRequiredItemPolicy(string value) =>
            value.Trim().ToLowerInvariant() switch
            {
                "keep" => ChecklistRequiredItemPolicy.Keep,
                "consume" => ChecklistRequiredItemPolicy.Consume,
                _ => ChecklistRequiredItemPolicy.None
            };

        public static ChecklistChainState ParseChainState(string value) =>
            value.Trim().ToLowerInvariant() switch
            {
                "normal" => ChecklistChainState.Normal,
                "target_lost" => ChecklistChainState.TargetLost,
                "manitto_lost" => ChecklistChainState.ManittoLost,
                "both_lost" => ChecklistChainState.BothLost,
                _ => ChecklistChainState.None
            };

        public static (int min, int max) ParseCountRange(string value)
        {
            var normalized = value.Trim();
            if (normalized.Contains("~"))
            {
                var parts = normalized.Split('~');
                return (int.Parse(parts[0]), int.Parse(parts[1]));
            }

            var count = int.Parse(normalized);
            return (count, count);
        }

        public static List<string> ParseTags(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split('|')
                    .Select(tag => tag.Trim())
                    .Where(tag => !string.IsNullOrEmpty(tag))
                    .ToList();
    }
}
