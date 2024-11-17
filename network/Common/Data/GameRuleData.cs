// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using network.common.data.helpers;
using network.common.data.models;
using network.managers;

namespace network.common.data
{
    public static class GameRuleData
    {
        private static float Speed { get; set; }
        public static float MoveElapsedTime { get; private set; }
        public static Cell StartPosition { get; private set; } = new(-1, -1);
        public static List<(int, int)> DefaultItemList { get; private set; } = null!;
        public static float SkillCompleteTime { get; private set; }
        public static bool HeartBeatActive { get; private set; } // TODO

        public static void Initialize(List<CsvRow> csvData)
        {
            var speedRow = csvData.First(row => row["id"] == "Speed");
            Speed = float.Parse(speedRow["value"]);
            MoveElapsedTime = 0.5f / Speed;

            var skillCompleteRow = csvData.First(row => row["id"] == "SkillCompleteTime");
            SkillCompleteTime = float.Parse(skillCompleteRow["value"]);

            var heartBeatRow = csvData.First(row => row["id"] == "HeartBeatActive");
            HeartBeatActive = int.Parse(heartBeatRow["value"]) == 1;

            var startPosRow = csvData.First(row => row["id"] == "StartPosition");
            var posStr = startPosRow["value"]
                .Trim('"')
                .Trim('(', ')')
                .Split(',');
            StartPosition = new Cell(
                int.Parse(posStr[0].Trim()),
                int.Parse(posStr[1].Trim())
            );

            var itemListRow = csvData.First(row => row["id"] == "DefaultItemList");
            var itemListStr = itemListRow["value"]
                .Trim('"')
                .Trim('[', ']');
            if (string.IsNullOrEmpty(itemListStr))
                DefaultItemList = new();
            else
                DefaultItemList = itemListStr.Split("),")
                    .Select(item =>
                    {
                        var parts = item.Trim('(', ')').Split(',');
                        return (int.Parse(parts[0].Trim()), int.Parse(parts[1].Trim()));
                    })
                    .ToList();
        }

        public static void Validate(LogManager logManager)
        {
            logManager.WriteInfoLog("=== GameRuleData Validation ===");
            logManager.WriteInfoLog($"Speed: {Speed}");
            logManager.WriteInfoLog($"StartPosition: ({StartPosition.X}, {StartPosition.Y})");
            logManager.WriteInfoLog($"SkillCompleteTime: {SkillCompleteTime}");
            logManager.WriteInfoLog($"HeartBeatActive: {HeartBeatActive}");
            logManager.WriteInfoLog(
                $"DefaultItemList: [{string.Join(", ", DefaultItemList.Select(x => $"({x.Item1}, {x.Item2})"))}]");
            logManager.WriteInfoLog("");

            var errors = new List<string>();

            if (Speed <= 0)
                errors.Add($"Speed must be greater than 0 (current: {Speed})");
            if (SkillCompleteTime < 0)
                errors.Add($"SkillCompleteTime cannot be negative (current: {SkillCompleteTime})");
            if (StartPosition.X < 0 || StartPosition.Y < 0)
                errors.Add(
                    $"StartPosition coordinates cannot be negative (current: ({StartPosition.X}, {StartPosition.Y}))");
            if (DefaultItemList.Count == 0)
                errors.Add("DefaultItemList is empty");

            if (errors.Count != 0)
            {
                logManager.WriteInfoLog("Validation Errors:");
                foreach (var error in errors) logManager.WriteInfoLog($"- {error}");
                throw new InvalidDataException(string.Join("\n", errors));
            }

            logManager.WriteInfoLog("All validations passed successfully!");
        }
    }
}