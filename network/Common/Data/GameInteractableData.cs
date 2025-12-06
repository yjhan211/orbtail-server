// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;
using network.common.data.models;
using network.managers;

namespace network.common.data
{
    public static class GameInteractableData
    {
        private static readonly Dictionary<int, InteractableInfoData> Infos = new();
        private static readonly Dictionary<int, List<InteractableInfoData>> InfosByZone = new();

        public static void Initialize(List<CsvRow> infoData, List<CsvRow> actionData)
        {
            // 액션 데이터를 id별로 그룹화
            var actionsByInteractId = actionData
                .GroupBy(row => int.Parse(row["id"]))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(row => int.Parse(row["action_id"]))
                          .Select(InteractableActionData.CreateFromData)
                          .ToList()
                );

            // 인터랙터블 정보 생성
            foreach (var row in infoData)
            {
                var info = InteractableInfoData.CreateFromData(row, actionsByInteractId);
                Infos[info.Id] = info;

                if (!InfosByZone.TryGetValue(info.ZoneId, out var list))
                {
                    list = new List<InteractableInfoData>();
                    InfosByZone[info.ZoneId] = list;
                }
                list.Add(info);
            }
        }

        public static InteractableInfoData Get(int id)
        {
            if (!Infos.TryGetValue(id, out var info))
            {
                return null;
            }
            return info;
        }

        public static List<InteractableInfoData> GetAll()
        {
            return Infos.Values.ToList();
        }

        public static List<InteractableInfoData> GetByZone(int zoneId)
        {
            if (!InfosByZone.TryGetValue(zoneId, out var list))
            {
                return new List<InteractableInfoData>();
            }
            return list;
        }

        public static void Validate(LogManager logManager)
        {
            LogManager.WriteDebugLog("=== GameInteractableData Validation ===");
            foreach (var (id, info) in Infos)
            {
                LogManager.WriteDebugLog($"[{id}] {info.Name} - Actions: {info.Actions.Count}");
            }
            LogManager.WriteDebugLog("All validations passed successfully!");
        }
    }

    public class InteractableInfoData
    {
        public int Id { get; private set; }
        public int ZoneId { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public List<InteractableActionData> Actions { get; private set; }

        public static InteractableInfoData CreateFromData(CsvRow row, Dictionary<int, List<InteractableActionData>> actionsByInteractId)
        {
            var id = int.Parse(row["id"]);

            return new InteractableInfoData
            {
                Id = id,
                ZoneId = int.Parse(row["area_type"]),
                Name = row["name"].Trim('"'),
                Description = row["description"].Trim('"').Replace("\\n", "\n"),
                Actions = actionsByInteractId.TryGetValue(id, out var actions) ? actions : new List<InteractableActionData>()
            };
        }
    }

    public class InteractableActionData
    {
        public int InteractId { get; private set; }
        public int ActionId { get; private set; }
        public string ActionText { get; private set; }
        public string ResultText { get; private set; }
        public RewardType RewardType { get; private set; }
        public int RewardId { get; private set; }

        public static InteractableActionData CreateFromData(CsvRow row)
        {
            return new InteractableActionData
            {
                InteractId = int.Parse(row["id"]),
                ActionId = int.Parse(row["action_id"]),
                ActionText = row["action_text"],
                ResultText = row["result_text"].Trim('"').Replace("\\n", "\n"),
                RewardType = (RewardType)int.Parse(row["reward_type"]),
                RewardId = int.Parse(row["reward_id"])
            };
        }
    }
}
