// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System.Collections.Generic;
using network.common.data.helpers;
using network.managers;

namespace network.common.data
{
    /// <summary>
    /// story_ark.csv 데이터 - 편지 내용 (정신 오염 시 수첩에 표시)
    /// </summary>
    public static class StoryArkData
    {
        private static readonly List<StoryArkEntry> _entries = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            _entries.Clear();

            foreach (var row in csvData)
            {
                var entry = StoryArkEntry.CreateFromData(row);
                _entries.Add(entry);
            }
        }

        public static StoryArkEntry Get(int id)
        {
            // id는 1부터 시작, 인덱스는 0부터
            var index = id - 1;
            if (index >= 0 && index < _entries.Count)
            {
                return _entries[index];
            }
            return null;
        }

        public static int Count => _entries.Count;

        /// <summary>
        /// 전체 편지 내용을 하나의 문자열로 반환
        /// </summary>
        public static string GetFullContent()
        {
            var result = new System.Text.StringBuilder();
            foreach (var entry in _entries)
            {
                if (result.Length > 0)
                {
                    result.Append(" ");
                }
                result.Append(entry.Content);
            }
            return result.ToString();
        }

        /// <summary>
        /// 특정 인덱스부터 지정된 길이만큼 편지 내용 반환
        /// </summary>
        public static string GetContentSlice(int startIndex, int length)
        {
            var fullContent = GetFullContent();
            if (startIndex >= fullContent.Length) return "";

            var endIndex = System.Math.Min(startIndex + length, fullContent.Length);
            return fullContent.Substring(startIndex, endIndex - startIndex);
        }

        public static List<StoryArkEntry> GetAll()
        {
            return new List<StoryArkEntry>(_entries);
        }

        public static void Validate(LogManager logManager)
        {
            LogManager.WriteDebugLog("=== StoryArkData Validation ===");
            LogManager.WriteDebugLog($"Total {_entries.Count} story entries loaded");
            LogManager.WriteDebugLog($"Full content length: {GetFullContent().Length} chars");
            LogManager.WriteDebugLog("StoryArkData validation completed!");
        }
    }

    public class StoryArkEntry
    {
        public int Id { get; private set; }
        public string Content { get; private set; }

        public static StoryArkEntry CreateFromData(CsvRow row)
        {
            return new StoryArkEntry
            {
                Id = int.Parse(row["id"]),
                Content = row["content"]
            };
        }
    }
}
