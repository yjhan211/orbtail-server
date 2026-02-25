// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System.Collections.Generic;
using System.IO;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.managers;

namespace network.common.data
{
    public static class GameAreaNameData
    {
        private static Dictionary<AreaType, LocalizedText> _areaNames = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            _areaNames.Clear();

            foreach (var row in csvData)
            {
                var areaType = (AreaType)int.Parse(row["area_type"]);
                var name = LocalizedText.FromCsv(row, "name");
                _areaNames[areaType] = name;
            }
        }

        public static void Validate(LogManager logManager)
        {
            LogManager.WriteInfoLog("=== GameAreaNameData Validation ===");
            LogManager.WriteInfoLog($"Total area names loaded: {_areaNames.Count}");

            var errors = new List<string>();

            // 모든 AreaType enum 값이 데이터에 존재하는지 확인
            foreach (AreaType areaType in System.Enum.GetValues(typeof(AreaType)))
            {
                if (!_areaNames.ContainsKey(areaType))
                {
                    errors.Add($"Missing area name for AreaType: {areaType}");
                }
                else
                {
                    LogManager.WriteInfoLog($"{areaType}: {_areaNames[areaType].Kr}");
                }
            }

            LogManager.WriteInfoLog("");

            if (errors.Count != 0)
            {
                LogManager.WriteInfoLog("Validation Errors:");
                foreach (var error in errors)
                {
                    LogManager.WriteInfoLog($"- {error}");
                }
                throw new InvalidDataException(string.Join("\n", errors));
            }

            LogManager.WriteInfoLog("All validations passed successfully!");
        }

        /// <summary>
        /// 영역 타입에 해당하는 이름을 가져옵니다
        /// </summary>
        public static string Get(AreaType areaType)
        {
            if (_areaNames.TryGetValue(areaType, out var name))
            {
                return name.Kr;
            }

            return "알 수 없는 영역";
        }

        /// <summary>
        /// 영역 타입에 해당하는 LocalizedText를 가져옵니다
        /// </summary>
        public static LocalizedText GetLocalizedText(AreaType areaType)
        {
            if (_areaNames.TryGetValue(areaType, out var name))
            {
                return name;
            }

            return null;
        }
    }
}
