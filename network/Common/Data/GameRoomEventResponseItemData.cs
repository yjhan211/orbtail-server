// ReSharper disable All
#pragma warning disable CS8618
#pragma warning disable CS8603

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using network.common.data.helpers;
using Newtonsoft.Json;

namespace network.common.data
{
    public static class GameRoomEventResponseItemData
    {
        private static readonly Dictionary<int, RoomEventResponseItemInfoData> _items = new();

        public static void Initialize(List<CsvRow> rows)
        {
            _items.Clear();
            foreach (var row in rows)
            {
                var item = RoomEventResponseItemInfoData.CreateFromData(row);
                if (item.ItemId > 0)
                    _items[item.ItemId] = item;
            }
        }

        public static RoomEventResponseItemInfoData Get(int itemId)
        {
            return _items.GetValueOrDefault(itemId);
        }

        public static IReadOnlyList<RoomEventResponseItemInfoData> GetAll()
        {
            return _items.Values.OrderBy(item => item.ItemId).ToList();
        }
    }

    public sealed class RoomEventResponseItemInfoData
    {
        public int ItemId { get; private set; }
        public HashSet<string> ResponseTags { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public int ResponsePower { get; private set; }

        public static RoomEventResponseItemInfoData CreateFromData(CsvRow row)
        {
            int responsePower = int.Parse(row["response_power"]);
            if (responsePower is not (1 or 3 or 5))
                throw new InvalidDataException($"Invalid room-event response_power: {responsePower}");

            var tags = JsonConvert.DeserializeObject<List<string>>(row["response_tags"]) ?? new List<string>();
            if (tags.Count == 0 || tags.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Room-event response item requires at least one non-empty tag");

            return new RoomEventResponseItemInfoData
            {
                ItemId = int.Parse(row["item_id"]),
                ResponseTags = tags.ToHashSet(StringComparer.OrdinalIgnoreCase),
                ResponsePower = responsePower
            };
        }
    }
}
