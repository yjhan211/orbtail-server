// ReSharper disable All
#pragma warning disable CS8618

using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GameStatusEffectData
    {
        private static readonly Dictionary<int, StatusEffectInfoData> _effectsById = new();
        private static readonly Dictionary<int, List<StatusEffectInfoData>> _effectsByBuffId = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            _effectsById.Clear();
            _effectsByBuffId.Clear();

            foreach (var effect in csvData.Select(StatusEffectInfoData.CreateFromData))
            {
                _effectsById[effect.Id] = effect;

                if (!_effectsByBuffId.TryGetValue(effect.BuffId, out var list))
                {
                    list = new List<StatusEffectInfoData>();
                    _effectsByBuffId[effect.BuffId] = list;
                }

                list.Add(effect);
            }
        }

        public static StatusEffectInfoData Get(int id)
        {
            if (!_effectsById.TryGetValue(id, out var effect))
                throw new KeyNotFoundException($"Status effect {id} not found");
            return effect;
        }

        public static bool TryGet(int id, out StatusEffectInfoData effect)
        {
            return _effectsById.TryGetValue(id, out effect);
        }

        public static IReadOnlyList<StatusEffectInfoData> GetByBuffId(int buffId)
        {
            return _effectsByBuffId.TryGetValue(buffId, out var list) ? list : new List<StatusEffectInfoData>();
        }

        public static IReadOnlyCollection<StatusEffectInfoData> GetAll()
        {
            return _effectsById.Values;
        }
    }

    public class StatusEffectInfoData
    {
        public int Id { get; private set; }
        public int BuffId { get; private set; }
        public string Kind { get; private set; }
        public int NameTextId { get; private set; }
        public int DescTextId { get; private set; }

        public static StatusEffectInfoData CreateFromData(CsvRow row)
        {
            return new StatusEffectInfoData
            {
                Id = int.Parse(row["id"]),
                BuffId = int.Parse(row["buff_id"]),
                Kind = row["kind"],
                NameTextId = int.Parse(row["name_text_id"]),
                DescTextId = int.Parse(row["desc_text_id"])
            };
        }
    }
}
