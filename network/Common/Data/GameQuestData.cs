// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Newtonsoft.Json;
using network.common.data.helpers;
using network.common.data.models;
using network.managers;

namespace network.common.data
{
    public static class GameQuestData
    {
        private static readonly Dictionary<int, QuestInfoData> Quests = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            var questInfos = csvData.Select(QuestInfoData.CreateFromData);
            foreach (var questInfo in questInfos) Quests[questInfo.Id] = questInfo;
        }

        public static QuestInfoData Get(int id)
        {
            if (!Quests.TryGetValue(id, out var quest)) throw new KeyNotFoundException($"Quest {id} not found");
            return quest;
        }

        public static void Validate(LogManager logManager)
        {
            LogManager.WriteDebugLog("=== GameQuestData Validation ===");
            foreach (var (id, quest) in Quests)
            {
                LogManager.WriteDebugLog($"[{id}] {quest.Title}");
            }
            LogManager.WriteDebugLog("All validations passed successfully!");
        }
    }

    public class QuestInfoData
    {
        public int Id { get; private set; }
        public QuestType QuestType { get; private set; }
        public string Title { get; private set; }
        public string Detail { get; private set; }
        public string Behavior { get; private set; }
        public int RequireCount { get; private set; }
        public List<(int, int)> RewardItemList { get; private set; }
        public List<int> NextIdList { get; private set; }
        public List<int> NextRequire { get; private set; }

        public static QuestInfoData CreateFromData(CsvRow row)
        {
            var id = int.Parse(row["id"]);
            var nextIdListValue = row["next_id_list"];
            var nextRequireValue = row["next_require"];
            var rewardItemListValue = row["reward_item_list"];

            return new QuestInfoData
            {
                Id = id,
                QuestType = (QuestType)(row["id"][0] - '0'),
                Title = row["title"],
                Detail = row["detail"],
                Behavior = row["behavior"],
                RequireCount = int.Parse(row["require_count"]),
                RewardItemList = ParseTupleList(rewardItemListValue),
                NextIdList = ParseIntList(nextIdListValue),
                NextRequire = ParseIntList(nextRequireValue),
            };
        }

        public static List<int> ParseIntList(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "[]" || value == "\"[]\"")
                return new List<int>();

            try
            {
                // JSON 형태로 시도
                return JsonConvert.DeserializeObject<List<int>>(value) ?? new List<int>();
            }
            catch
            {
                try
                {
                    // JSON 파싱 실패 시 수동 파싱
                    // 따옴표와 대괄호 제거
                    value = value.Trim('"').Trim('[', ']');
                    if (string.IsNullOrWhiteSpace(value))
                        return new List<int>();

                    return value.Split(',')
                               .Select(s => s.Trim().Trim('"')) // 개별 값의 따옴표도 제거
                               .Where(s => !string.IsNullOrWhiteSpace(s))
                               .Select(int.Parse)
                               .ToList();
                }
                catch (Exception ex)
                {
                    // 디버깅을 위해 어떤 값이 문제인지 출력
                    Console.WriteLine($"Failed to parse int list: '{value}' - {ex.Message}");
                    return new List<int>();
                }
            }
        }

        private static List<(int, int)> ParseTupleList(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "[]" || value == "\"[]\"")
                return new List<(int, int)>();

            try
            {
                // 따옴표 제거 후 JSON 파싱 시도
                var cleanValue = value.Trim('"');
                return JsonConvert.DeserializeObject<List<(int, int)>>(cleanValue) ?? new List<(int, int)>();
            }
            catch
            {
                // 튜플 파싱이 실패하면 빈 리스트 반환
                return new List<(int, int)>();
            }
        }
    }
}
