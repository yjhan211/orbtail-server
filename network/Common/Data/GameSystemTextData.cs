// ReSharper disable All
#pragma warning disable CS8618

using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GameSystemTextData
    {
        private static readonly Dictionary<int, SystemTextData> Texts = new();
        private static readonly Dictionary<SystemTextCategory, List<SystemTextData>> TextsByCategory = new();

        public static void Initialize(List<CsvRow> data)
        {
            foreach (var row in data)
            {
                var text = SystemTextData.CreateFromData(row);
                Texts[text.Id] = text;

                if (!TextsByCategory.TryGetValue(text.Category, out var list))
                {
                    list = new List<SystemTextData>();
                    TextsByCategory[text.Category] = list;
                }
                list.Add(text);
            }
        }

        public static SystemTextData Get(int id)
        {
            return Texts.GetValueOrDefault(id);
        }

        public static string GetText(int id)
        {
            var data = Get(id);
            return data?.TextKo ?? "";
        }

        public static List<SystemTextData> GetByCategory(SystemTextCategory category)
        {
            return TextsByCategory.GetValueOrDefault(category) ?? new List<SystemTextData>();
        }

        public static List<SystemTextData> GetAll()
        {
            return Texts.Values.ToList();
        }
    }

    public class SystemTextData
    {
        public int Id { get; private set; }
        public SystemTextCategory Category { get; private set; }
        public string TextKo { get; private set; }

        public static SystemTextData CreateFromData(CsvRow row)
        {
            return new SystemTextData
            {
                Id = int.Parse(row["id"]),
                Category = (SystemTextCategory)int.Parse(row["category"]),
                TextKo = row["text_ko"].Trim('"').Replace("\\n", "\n")
            };
        }
    }
}
