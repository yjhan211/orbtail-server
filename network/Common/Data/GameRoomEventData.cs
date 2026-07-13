// ReSharper disable All
#pragma warning disable CS8618
#pragma warning disable CS8601
#pragma warning disable CS8603

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using network.common;
using network.common.data.helpers;
using Newtonsoft.Json;

namespace network.common.data
{
    public static class GameRoomEventData
    {
        private static readonly Dictionary<int, RoomEventInfoData> _eventsById = new();
        private static readonly Dictionary<AreaType, List<RoomEventInfoData>> _eventsByArea = new();

        public static void Initialize(List<CsvRow> masterData, List<CsvRow> choiceData)
        {
            _eventsById.Clear();
            _eventsByArea.Clear();

            var choicesByEventId = choiceData
                .Select(RoomEventChoiceInfoData.CreateFromData)
                .Where(choice => choice.ChoiceId > 0)
                .GroupBy(choice => choice.EventId)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderBy(choice => choice.SortOrder)
                        .ThenBy(choice => choice.ChoiceId)
                        .ToList());

            foreach (var row in masterData)
            {
                int eventId = int.Parse(row["event_id"]);
                var roomEvent = RoomEventInfoData.CreateFromData(
                    row,
                    choicesByEventId.TryGetValue(eventId, out var choices)
                        ? choices
                        : new List<RoomEventChoiceInfoData>());
                Register(roomEvent);
            }

            SortByArea();
        }

        public static void Initialize(List<CsvRow> csvData)
        {
            _eventsById.Clear();
            _eventsByArea.Clear();

            foreach (var group in csvData.GroupBy(row => int.Parse(row["event_id"])))
            {
                var roomEvent = RoomEventInfoData.CreateFromRows(group.ToList());
                Register(roomEvent);
            }

            SortByArea();
        }

        private static void Register(RoomEventInfoData roomEvent)
        {
            _eventsById[roomEvent.EventId] = roomEvent;

            foreach (var areaType in roomEvent.AreaTypes)
            {
                if (!_eventsByArea.TryGetValue(areaType, out var list))
                {
                    list = new List<RoomEventInfoData>();
                    _eventsByArea[areaType] = list;
                }

                list.Add(roomEvent);
            }
        }

        private static void SortByArea()
        {
            foreach (var list in _eventsByArea.Values)
                list.Sort((a, b) => a.EventId.CompareTo(b.EventId));
        }

        public static RoomEventInfoData Get(int eventId)
        {
            return _eventsById.TryGetValue(eventId, out var roomEvent) ? roomEvent : null;
        }

        public static bool TryGet(int eventId, out RoomEventInfoData roomEvent)
        {
            return _eventsById.TryGetValue(eventId, out roomEvent);
        }

        public static IReadOnlyList<RoomEventInfoData> GetByArea(AreaType areaType)
        {
            return _eventsByArea.TryGetValue(areaType, out var events) ? events : new List<RoomEventInfoData>();
        }

        public static bool TryGetChoice(int eventId, int choiceId,
            out RoomEventInfoData roomEvent, out RoomEventChoiceInfoData choice)
        {
            choice = null;
            if (!TryGet(eventId, out roomEvent) || roomEvent?.Choices == null)
                return false;

            choice = roomEvent.Choices.FirstOrDefault(candidate => candidate.ChoiceId == choiceId);
            return choice != null;
        }
    }

    public class RoomEventInfoData
    {
        public int EventId { get; private set; }
        public List<AreaType> AreaTypes { get; private set; } = new();
        public LocalizedText Title { get; private set; }
        public LocalizedText Description { get; private set; }
        public string TriggerType { get; private set; }
        public int ProbabilityPercent { get; private set; }
        public int TimeoutSeconds { get; private set; }
        public int DefaultChoiceId { get; private set; }
        public bool UsesWorldState { get; private set; }
        public List<string> ResponseTagPool { get; private set; } = new();
        public int TargetContribution { get; private set; }
        public int DurationSeconds { get; private set; }
        public int MaxContributionPerPlayer { get; private set; }
        public int MaxTimeExtensions { get; private set; }
        public int TimeExtensionSeconds { get; private set; }
        public int MaxManualResponses { get; private set; }
        public List<RoomEventChoiceInfoData> Choices { get; private set; } = new();

        public static RoomEventInfoData CreateFromRows(List<CsvRow> rows)
        {
            var first = rows[0];
            var choices = rows
                .Select(RoomEventChoiceInfoData.CreateFromData)
                .Where(choice => choice.ChoiceId > 0)
                .OrderBy(choice => choice.SortOrder)
                .ThenBy(choice => choice.ChoiceId)
                .ToList();

            return CreateFromData(first, choices);
        }

        public static RoomEventInfoData CreateFromData(CsvRow row, List<RoomEventChoiceInfoData> choices)
        {
            var areaValues = JsonConvert.DeserializeObject<List<int>>(row["area_types"]) ?? new List<int>();

            return new RoomEventInfoData
            {
                EventId = int.Parse(row["event_id"]),
                AreaTypes = areaValues.Select(value => (AreaType)value).ToList(),
                Title = LocalizedText.FromCsv(row, "title"),
                Description = LocalizedText.FromCsvMultiline(row, "description"),
                TriggerType = row.ContainsKey("trigger_type") ? row["trigger_type"].Trim() : "explore",
                ProbabilityPercent = ParseInt(row, "probability", 0),
                TimeoutSeconds = ParseInt(row, "timeout_seconds", 0),
                DefaultChoiceId = ParseInt(row, "default_choice_id", 0),
                UsesWorldState = ParseBool(row, "uses_world_state"),
                ResponseTagPool = ParseStringList(row, "response_tag_pool"),
                TargetContribution = ParseInt(row, "target_contribution", 0),
                DurationSeconds = ParseInt(row, "duration_seconds", 0),
                MaxContributionPerPlayer = ParseInt(row, "max_contribution_per_player", 0),
                MaxTimeExtensions = ParseInt(row, "max_time_extensions", 0),
                TimeExtensionSeconds = ParseInt(row, "time_extension_seconds", 0),
                MaxManualResponses = ParseInt(row, "max_manual_responses", 0),
                Choices = choices ?? new List<RoomEventChoiceInfoData>()
            };
        }

        private static int ParseInt(CsvRow row, string key, int fallback)
        {
            return row.ContainsKey(key) && int.TryParse(row[key], out int value) ? value : fallback;
        }

        private static bool ParseBool(CsvRow row, string key)
        {
            return row.ContainsKey(key) && bool.TryParse(row[key], out bool value) && value;
        }

        private static List<string> ParseStringList(CsvRow row, string key)
        {
            if (!row.ContainsKey(key) || string.IsNullOrWhiteSpace(row[key]))
                return new List<string>();

            return JsonConvert.DeserializeObject<List<string>>(row[key]) ?? new List<string>();
        }
    }

    public class RoomEventChoiceInfoData
    {
        public int ChoiceId { get; private set; }
        public int EventId { get; private set; }
        public int SortOrder { get; private set; }
        public LocalizedText Label { get; private set; }
        public LocalizedText ResultText { get; private set; }
        public string ChoiceType { get; private set; }
        public string RequirementType { get; private set; }
        public string RequirementValue { get; private set; }
        public string WorldEffectId { get; private set; }
        public int ConsumeItemCount { get; private set; }
        public string StatePrecondition { get; private set; }
        public bool ConsumeItem { get; private set; }
        public int MentalDelta { get; private set; }
        public int StaminaDelta { get; private set; }
        public int RewardItemId { get; private set; }
        public int RewardItemCount { get; private set; }
        public string ClueRewardId { get; private set; }
        public int LuckSuccessRate { get; private set; }
        public int LuckFailureChoiceId { get; private set; }

        public static RoomEventChoiceInfoData CreateFromData(CsvRow row)
        {
            int consumeItemCount = ParseConsumeItemCount(row);

            return new RoomEventChoiceInfoData
            {
                ChoiceId = ParseInt(row, "choice_id", 0),
                EventId = ParseInt(row, "event_id", 0),
                SortOrder = ParseInt(row, "sort_order", 0),
                Label = LocalizedText.FromCsv(row, "choice_text"),
                ResultText = LocalizedText.FromCsvMultiline(row, "result_text"),
                ChoiceType = Read(row, "choice_type"),
                RequirementType = Read(row, "requirement_type"),
                RequirementValue = Read(row, "requirement_value"),
                WorldEffectId = Read(row, "world_effect_id"),
                ConsumeItemCount = consumeItemCount,
                StatePrecondition = Read(row, "state_precondition"),
                ConsumeItem = consumeItemCount > 0,
                MentalDelta = ParseInt(row, "mental_delta", 0),
                StaminaDelta = ParseInt(row, "stamina_delta", 0),
                RewardItemId = ParseInt(row, "reward_item_id", 0),
                RewardItemCount = ParseInt(row, "reward_item_count", 0),
                ClueRewardId = Read(row, "clue_reward_id"),
                LuckSuccessRate = ParseInt(row, "luck_success_rate", 0),
                LuckFailureChoiceId = ParseInt(row, "luck_failure_choice_id", 0)
            };
        }

        private static string Read(CsvRow row, string key)
        {
            return row.ContainsKey(key) ? row[key]?.Trim() ?? "" : "";
        }

        private static int ParseInt(CsvRow row, string key, int fallback)
        {
            return row.ContainsKey(key) && int.TryParse(row[key], out int value) ? value : fallback;
        }

        private static bool ParseBool(CsvRow row, string key)
        {
            return row.ContainsKey(key) && bool.TryParse(row[key], out bool value) && value;
        }

        private static int ParseConsumeItemCount(CsvRow row)
        {
            int value = row.ContainsKey("consume_item_count") &&
                        int.TryParse(row["consume_item_count"], out int parsed)
                ? parsed
                : ParseBool(row, "consume_item") ? 1 : 0;
            if (value is < 0 or > 1)
                throw new InvalidDataException($"consume_item_count must be 0 or 1, got {value}");
            return value;
        }
    }
}
