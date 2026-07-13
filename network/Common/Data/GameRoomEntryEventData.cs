// ReSharper disable All
#pragma warning disable CS8618
#pragma warning disable CS8601

using System.Collections.Generic;
using System.Linq;
using network.common;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GameRoomEntryEventData
    {
        private static readonly Dictionary<int, RoomEntryEventInfoData> _eventsById = new();
        private static readonly Dictionary<AreaType, List<RoomEntryEventInfoData>> _eventsByArea = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            _eventsById.Clear();
            _eventsByArea.Clear();

            foreach (var row in csvData)
            {
                var entryEvent = RoomEntryEventInfoData.CreateFromData(row);
                _eventsById[entryEvent.Id] = entryEvent;

                if (!_eventsByArea.TryGetValue(entryEvent.AreaType, out var list))
                {
                    list = new List<RoomEntryEventInfoData>();
                    _eventsByArea[entryEvent.AreaType] = list;
                }

                list.Add(entryEvent);
            }
        }

        public static RoomEntryEventInfoData Get(int id)
        {
            return _eventsById.TryGetValue(id, out var entryEvent) ? entryEvent : null;
        }

        public static bool TryGet(int id, out RoomEntryEventInfoData entryEvent)
        {
            return _eventsById.TryGetValue(id, out entryEvent);
        }

        public static bool TryGetByArea(AreaType areaType, out RoomEntryEventInfoData entryEvent)
        {
            if (_eventsByArea.TryGetValue(areaType, out var events) && events.Count > 0)
            {
                entryEvent = events[0];
                return true;
            }

            entryEvent = null;
            return false;
        }

        public static bool TryGetChoice(int eventId, int choiceId,
            out RoomEntryEventInfoData entryEvent, out RoomEntryEventChoiceInfoData choice)
        {
            choice = null;
            if (!TryGet(eventId, out entryEvent) || entryEvent?.Choices == null)
                return false;

            choice = entryEvent.Choices.FirstOrDefault(candidate => candidate.ChoiceId == choiceId);
            return choice != null;
        }

        public static IReadOnlyList<RoomEntryEventInfoData> GetByArea(AreaType areaType)
        {
            return _eventsByArea.TryGetValue(areaType, out var events) ? events : new List<RoomEntryEventInfoData>();
        }

        public static IReadOnlyCollection<RoomEntryEventInfoData> GetAll()
        {
            return _eventsById.Values.ToList();
        }
    }

    public class RoomEntryEventInfoData
    {
        public int Id { get; private set; }
        public AreaType AreaType { get; private set; }
        public LocalizedText Title { get; private set; }
        public LocalizedText Description { get; private set; }
        public List<RoomEntryEventChoiceInfoData> Choices { get; private set; } = new();

        public static RoomEntryEventInfoData CreateFromData(CsvRow row)
        {
            return new RoomEntryEventInfoData
            {
                Id = int.Parse(row["id"]),
                AreaType = (AreaType)int.Parse(row["area_type"]),
                Title = LocalizedText.FromCsv(row, "title"),
                Description = LocalizedText.FromCsvMultiline(row, "description"),
                Choices = RoomEntryEventChoiceInfoData.CreateChoices(row)
            };
        }
    }

    public class RoomEntryEventChoiceInfoData
    {
        public int ChoiceId { get; private set; }
        public LocalizedText Label { get; private set; }
        public LocalizedText ResultText { get; private set; }
        public string GrantedTraitId { get; private set; }

        public static List<RoomEntryEventChoiceInfoData> CreateChoices(CsvRow row)
        {
            var choices = new List<RoomEntryEventChoiceInfoData>();
            for (int index = 1; index <= 3; index++)
            {
                string idKey = $"choice{index}_id";
                if (!row.ContainsKey(idKey) || !int.TryParse(row[idKey], out int choiceId) || choiceId <= 0)
                    continue;

                choices.Add(new RoomEntryEventChoiceInfoData
                {
                    ChoiceId = choiceId,
                    Label = LocalizedText.FromCsv(row, $"choice{index}_label"),
                    ResultText = LocalizedText.FromCsvMultiline(row, $"choice{index}_result"),
                    GrantedTraitId = row.ContainsKey($"choice{index}_trait") ? row[$"choice{index}_trait"].Trim() : ""
                });
            }

            return choices;
        }
    }
}
