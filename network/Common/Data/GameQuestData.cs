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
            foreach (var questInfo in questInfos) Quests.Add(questInfo.Id, questInfo);
        }

        public static QuestInfoData Get(int id)
        {
            if (!Quests.TryGetValue(id, out var quest)) throw new KeyNotFoundException($"Quest {id} not found");
            return quest;
        }

        public static void Validate(LogManager logManager)
        {
            logManager.WriteDebugLog("=== GameQuestData Validation ===");
            foreach (var (id, quest) in Quests)
            {
                logManager.WriteDebugLog($"[{id}] {quest.Title}");
            }
            logManager.WriteDebugLog("All validations passed successfully!");
        }
    }

    public class QuestInfoData
    {
        public int Id { get; private set; }
        public QuestType QuestType { get; private set; }
        public string Title { get; private set; }
        public string Detail { get; private set; }
        public int RequireCount { get; private set; }
        public List<(int, int)> RewardItemList { get; private set; }
        public List<int> NextIdList { get; private set; }

        public static QuestInfoData CreateFromData(CsvRow row)
        {
            return new QuestInfoData
            {
                Id = int.Parse(row["id"]),
                QuestType = (QuestType)Enum.Parse(typeof(QuestType), row["type"]),
                Title = row["title"],
                Detail = row["detail"],
                RequireCount = int.Parse(row["require_count"]),
                RewardItemList = JsonConvert.DeserializeObject<List<(int, int)>>(row["reward_item_list"]) ?? new(),
                NextIdList = JsonConvert.DeserializeObject<List<int>>(row["next_id_list"]) ?? new()
            };
        }
    }
}
