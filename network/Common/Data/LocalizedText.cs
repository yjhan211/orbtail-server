// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System.Collections.Generic;
using network.common.data.helpers;

namespace network.common.data
{
    /// <summary>
    ///     다국어 텍스트 컨테이너. Dictionary 기반 저장으로 임의 언어 코드 자유 추가 가능.
    ///     CSV에 {prefix}_{lang} 컬럼을 추가하면 SupportedLanguages에 lang을 등록만 하면 자동 인식.
    /// </summary>
    public class LocalizedText
    {
        /// <summary>
        ///     CSV에서 자동 감지할 언어 코드 목록. 신규 언어 추가 시 이 배열에만 코드를 추가하면 됨.
        ///     (대응 CSV에 {prefix}_{lang} 컬럼이 있어야 한다)
        /// </summary>
        public static readonly string[] SupportedLanguages = { "kr", "en", "jp" };

        private const string FallbackLang = "kr";

        private readonly Dictionary<string, string> _texts;

        /// <summary>
        ///     호환 생성자 — 기존 (kr, en) 호출자 유지. jp 추가 가능.
        /// </summary>
        public LocalizedText(string kr, string en = "", string jp = "")
        {
            _texts = new Dictionary<string, string>
            {
                ["kr"] = kr ?? "",
                ["en"] = en ?? "",
                ["jp"] = jp ?? ""
            };
        }

        public LocalizedText(IDictionary<string, string> texts)
        {
            _texts = new Dictionary<string, string>();
            if (texts == null) return;

            foreach (var kvp in texts)
                _texts[kvp.Key] = kvp.Value ?? "";
        }

        public string Kr => Get("kr");
        public string En => Get("en");
        public string Jp => Get("jp");

        /// <summary>
        ///     지정 언어의 텍스트 반환. 빈 값/미등록 시 한국어 fallback.
        /// </summary>
        public string Get(string lang)
        {
            if (_texts.TryGetValue(lang, out var text) && !string.IsNullOrEmpty(text))
                return text;

            return _texts.TryGetValue(FallbackLang, out var fallback) ? fallback ?? "" : "";
        }

        /// <summary>
        ///     호환용 별칭. 기존 호출자(GetText) 유지.
        /// </summary>
        public string GetText(string lang = FallbackLang) => Get(lang);

        /// <summary>
        ///     CsvRow에서 {prefix}_{lang} 컬럼들을 자동 감지해 LocalizedText 생성.
        ///     SupportedLanguages 배열에 등록된 언어 중 컬럼이 존재하는 것만 채움.
        /// </summary>
        public static LocalizedText FromCsv(CsvRow row, string prefix)
        {
            var texts = new Dictionary<string, string>();
            foreach (var lang in SupportedLanguages)
            {
                var col = $"{prefix}_{lang}";
                if (row.ContainsKey(col))
                    texts[lang] = row[col] ?? "";
            }

            return new LocalizedText(texts);
        }

        /// <summary>
        ///     FromCsv와 동일하나 CSV의 \n 이스케이프(문자 그대로의 백슬래시-n)를 실제 개행 문자로 치환한다.
        ///     설명/결과 텍스트처럼 줄바꿈이 포함된 컬럼에 사용.
        /// </summary>
        public static LocalizedText FromCsvMultiline(CsvRow row, string prefix)
        {
            var texts = new Dictionary<string, string>();
            foreach (var lang in SupportedLanguages)
            {
                var col = $"{prefix}_{lang}";
                if (row.ContainsKey(col))
                    texts[lang] = (row[col] ?? "").Replace("\\n", "\n");
            }

            return new LocalizedText(texts);
        }

        public override string ToString()
        {
            return Kr;
        }
    }
}
