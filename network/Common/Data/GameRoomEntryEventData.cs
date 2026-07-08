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
        public LocalizedText AcceptLabel { get; private set; }
        public LocalizedText RejectLabel { get; private set; }

        public static RoomEntryEventInfoData CreateFromData(CsvRow row)
        {
            return new RoomEntryEventInfoData
            {
                Id = int.Parse(row["id"]),
                AreaType = (AreaType)int.Parse(row["area_type"]),
                Title = LocalizedText.FromCsv(row, "title"),
                Description = LocalizedText.FromCsvMultiline(row, "description"),
                AcceptLabel = LocalizedText.FromCsv(row, "accept_label"),
                RejectLabel = LocalizedText.FromCsv(row, "reject_label")
            };
        }
    }
}