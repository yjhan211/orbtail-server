using System.Collections.Generic;
using network.common.data.helpers;

namespace network.common.data
{
    /// <summary>
    ///     text_key → 다국어 텍스트 중앙 테이블 (localization.csv).
    ///     데이터 CSV가 인라인 kr/en/jp 대신 {prefix}_key로 텍스트를 참조할 때 사용한다.
    ///     (system_text와 목적이 같지만, 콘텐츠 로컬라이징 풀로 분리)
    /// </summary>
    public static class GameLocalizationData
    {
        private static readonly Dictionary<string, LocalizedText> _texts = new();

        public static void Initialize(List<CsvRow> rows)
        {
            _texts.Clear();
            foreach (var row in rows)
            {
                var key = row.ContainsKey("text_key") ? row["text_key"]?.Trim() ?? "" : "";
                if (string.IsNullOrEmpty(key)) continue;

                _texts[key] = new LocalizedText(
                    Unescape(row, "kr"),
                    Unescape(row, "en"),
                    Unescape(row, "jp"));
            }
        }

        public static LocalizedText Get(string key) =>
            !string.IsNullOrEmpty(key) && _texts.TryGetValue(key, out var text)
                ? text
                : new LocalizedText("", "", "");

        public static bool Has(string key) =>
            !string.IsNullOrEmpty(key) && _texts.ContainsKey(key);

        // CSV의 \n 이스케이프(문자 그대로의 백슬래시-n)를 실제 개행으로 치환.
        private static string Unescape(CsvRow row, string col) =>
            (row.ContainsKey(col) ? row[col] ?? "" : "").Replace("\\n", "\n");
    }
}
