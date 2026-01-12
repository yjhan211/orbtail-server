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

        /// <summary>
        /// 플레이스홀더를 치환한 텍스트 반환
        /// 지원 플레이스홀더: {Item.Name}, {Item.SpawnArea}, {Item.Warning}, {Spot.Name}, {Debuff.Warning}, {Condition.Text}
        /// </summary>
        public static string FormatText(int textId, TextReplacementContext context)
        {
            var template = GetText(textId);
            if (string.IsNullOrEmpty(template))
                return "";

            return FormatTemplate(template, context);
        }

        /// <summary>
        /// 템플릿 문자열에 플레이스홀더 치환
        /// </summary>
        public static string FormatTemplate(string template, TextReplacementContext context)
        {
            if (string.IsNullOrEmpty(template))
                return "";

            var result = template;

            // Item 관련 치환
            if (context.ItemName != null)
                result = result.Replace("{Item.Name}", context.ItemName);
            if (context.ItemSpawnArea != null)
                result = result.Replace("{Item.SpawnArea}", context.ItemSpawnArea);
            if (context.ItemWarning != null)
                result = result.Replace("{Item.Warning}", context.ItemWarning);

            // Spot 관련 치환
            if (context.SpotName != null)
                result = result.Replace("{Spot.Name}", context.SpotName);

            // Debuff 관련 치환
            if (context.DebuffWarning != null)
                result = result.Replace("{Debuff.Warning}", context.DebuffWarning);

            // Condition 관련 치환
            if (context.ConditionText != null)
                result = result.Replace("{Condition.Text}", context.ConditionText);

            return result;
        }
    }

    /// <summary>
    /// 텍스트 플레이스홀더 치환용 컨텍스트
    /// </summary>
    public class TextReplacementContext
    {
        public string ItemName { get; set; }
        public string ItemSpawnArea { get; set; }
        public string ItemWarning { get; set; }
        public string SpotName { get; set; }
        public string DebuffWarning { get; set; }
        public string ConditionText { get; set; }

        public static TextReplacementContext Create() => new TextReplacementContext();

        public TextReplacementContext WithItem(string name, string spawnArea = null, string warning = null)
        {
            ItemName = name;
            ItemSpawnArea = spawnArea;
            ItemWarning = warning;
            return this;
        }

        public TextReplacementContext WithSpot(string name)
        {
            SpotName = name;
            return this;
        }

        public TextReplacementContext WithDebuff(string warning)
        {
            DebuffWarning = warning;
            return this;
        }

        public TextReplacementContext WithCondition(string text)
        {
            ConditionText = text;
            return this;
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
