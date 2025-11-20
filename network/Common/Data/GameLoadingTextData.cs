// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System.Collections.Generic;
using System.IO;
using network.common.data.helpers;
using network.managers;
using UnityEngine;

namespace network.common.data
{
    public static class GameLoadingTextData
    {
        private static List<string> _loadingTexts;

        public static void Initialize(List<CsvRow> csvData)
        {
            _loadingTexts = new List<string>();

            foreach (var row in csvData)
            {
                var text = row["kr"];
                // 따옴표 제거
                if (text.StartsWith("\"") && text.EndsWith("\""))
                {
                    text = text.Substring(1, text.Length - 2);
                }
                _loadingTexts.Add(text);
            }
        }

        public static string GetRandomText()
        {
            if (_loadingTexts == null)
            {
                Debug.LogError("[GameLoadingTextData] _loadingTexts is null! Data not initialized.");
                return "로딩 중...";
            }

            if (_loadingTexts.Count == 0)
            {
                Debug.LogError("[GameLoadingTextData] _loadingTexts is empty!");
                return "로딩 중...";
            }

            var randomIndex = Random.Range(0, _loadingTexts.Count);
            Debug.Log($"[GameLoadingTextData] Total texts: {_loadingTexts.Count}, Selected index: {randomIndex}");
            return _loadingTexts[randomIndex];
        }

        public static void Validate(LogManager logManager)
        {
            logManager.WriteInfoLog("=== GameLoadingTextData Validation ===");
            logManager.WriteInfoLog($"Total loading texts: {_loadingTexts?.Count ?? 0}");

            var errors = new List<string>();

            if (_loadingTexts == null || _loadingTexts.Count == 0)
            {
                errors.Add("LoadingTexts list is empty or null");
            }

            if (errors.Count != 0)
            {
                logManager.WriteInfoLog("Validation Errors:");
                foreach (var error in errors)
                {
                    logManager.WriteInfoLog($"- {error}");
                }
                throw new InvalidDataException(string.Join("\n", errors));
            }

            logManager.WriteInfoLog("All validations passed successfully!");
            logManager.WriteInfoLog("");
        }
    }
}
