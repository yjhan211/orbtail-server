// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.IO;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GameLoadingTextData
    {
        private static List<LocalizedText> _loadingTexts;
        private static readonly Random _random = new Random();

        public static void Initialize(List<CsvRow> csvData)
        {
            _loadingTexts = new List<LocalizedText>();

            foreach (var row in csvData)
            {
                _loadingTexts.Add(LocalizedText.FromCsv(row, "text"));
            }
        }

        public static string GetRandomText()
        {
            return GetRandomText("kr");
        }

        /// <summary>
        ///     지정 언어로 랜덤 로딩 텍스트 반환. 빈 값/미등록 언어는 한국어 fallback.
        /// </summary>
        public static string GetRandomText(string lang)
        {
            if (_loadingTexts == null || _loadingTexts.Count == 0)
            {
                return "당신의 눈이 당신을 속이기 시작했다면,\n그것은 시스템 오류가 아닙니다.";
            }

            var randomIndex = _random.Next(0, _loadingTexts.Count);
            return _loadingTexts[randomIndex].Get(lang);
        }

        /// <summary>
        ///     랜덤 로딩 LocalizedText 반환. 호출자가 자기 시점 언어로 Get(lang) 가능.
        /// </summary>
        public static LocalizedText GetRandomLocalizedText()
        {
            if (_loadingTexts == null || _loadingTexts.Count == 0) return null;
            var randomIndex = _random.Next(0, _loadingTexts.Count);
            return _loadingTexts[randomIndex];
        }

        public static void Validate(Action<string> log)
        {
            log("=== GameLoadingTextData Validation ===");
            log($"Total loading texts: {_loadingTexts?.Count ?? 0}");

            var errors = new List<string>();

            if (_loadingTexts == null || _loadingTexts.Count == 0)
            {
                errors.Add("LoadingTexts list is empty or null");
            }

            if (errors.Count != 0)
            {
                log("Validation Errors:");
                foreach (var error in errors)
                {
                    log($"- {error}");
                }
                throw new InvalidDataException(string.Join("\n", errors));
            }

            log("All validations passed successfully!");
            log("");
        }
    }
}
