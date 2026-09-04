// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using network.common.data.helpers;
using network.common.data.models;

namespace network.common.data
{
    public static class GameRuleData
    {
        public static Cell StartPosition { get; private set; } = new(-1, -1);
        public static List<(int, int)> DefaultItemList { get; private set; } = null!;
        public static List<(int, int)> InGameItemList { get; private set; } = null!;
        public static bool HeartBeatActive { get; private set; }

        public static void Initialize(List<CsvRow> csvData)
        {
            var heartBeatRow = csvData.First(row => row["id"] == "HeartBeatActive");
            HeartBeatActive = int.Parse(heartBeatRow["value"]) == 1;

            var startPosRow = csvData.First(row => row["id"] == "StartPosition");
            var posStr = startPosRow["value"]
                .Trim('(', ')')
                .Split(',');
            StartPosition = new Cell(
                int.Parse(posStr[0].Trim()),
                int.Parse(posStr[1].Trim())
            );

            var itemListRow = csvData.First(row => row["id"] == "DefaultItemList");
            var itemListStr = itemListRow["value"]
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

            var inGameItemListRow = csvData.FirstOrDefault(row => row["id"] == "InGameItemList");
            if (inGameItemListRow == null)
            {
                InGameItemList = new();
            }
            else
            {
                var inGameItemListStr = inGameItemListRow["value"]
                    .Trim('[', ']');
                if (string.IsNullOrEmpty(inGameItemListStr))
                    InGameItemList = new();
                else
                    InGameItemList = inGameItemListStr.Split("),")
                        .Select(item =>
                        {
                            var parts = item.Trim('(', ')').Split(',');
                            return (int.Parse(parts[0].Trim()), int.Parse(parts[1].Trim()));
                        })
                        .ToList();
            }
        }

        public static void Validate(Action<string> log)
        {
            log("=== GameRuleData Validation ===");
            log($"StartPosition: ({StartPosition.X}, {StartPosition.Y})");
            log($"HeartBeatActive: {HeartBeatActive}");
            log(
                $"DefaultItemList: [{string.Join(", ", DefaultItemList.Select(x => $"({x.Item1}, {x.Item2})"))}]");
            log(
                $"InGameItemList: [{string.Join(", ", InGameItemList.Select(x => $"({x.Item1}, {x.Item2})"))}]");
            log("");

            var errors = new List<string>();

            if (StartPosition.X < 0 || StartPosition.Y < 0)
                errors.Add(
                    $"StartPosition coordinates cannot be negative (current: ({StartPosition.X}, {StartPosition.Y}))");
            if (DefaultItemList.Count == 0)
                errors.Add("DefaultItemList is empty");

            if (errors.Count != 0)
            {
                log("Validation Errors:");
                foreach (var error in errors) log($"- {error}");
                throw new InvalidDataException(string.Join("\n", errors));
            }

            log("All validations passed successfully!");
        }
    }
}
