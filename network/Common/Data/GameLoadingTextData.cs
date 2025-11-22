// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.IO;
using network.common.data.helpers;
using network.managers;

namespace network.common.data
{
    public static class GameLoadingTextData
    {
        private static List<string> _loadingTexts;
        private static readonly Random _random = new Random();

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
                UnityEngine.Debug.LogWarning("[GameLoadingTextData] _loadingTexts is null! Using fallback text.");
                return "당신의 눈이 당신을 속이기 시작했다면,\n그것은 시스템 오류가 아닙니다.";
            }

            if (_loadingTexts.Count == 0)
            {
                UnityEngine.Debug.LogWarning("[GameLoadingTextData] _loadingTexts is empty! Using fallback text.");
                return "당신의 눈이 당신을 속이기 시작했다면,\n그것은 시스템 오류가 아닙니다.";
            }

            var randomIndex = _random.Next(0, _loadingTexts.Count);
            var selectedText = _loadingTexts[randomIndex];
            UnityEngine.Debug.Log($"[GameLoadingTextData] Selected text #{randomIndex}/{_loadingTexts.Count}: '{selectedText.Substring(0, Math.Min(30, selectedText.Length))}...'");
            return selectedText;
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
