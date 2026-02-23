// ReSharper disable All
#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using network.common.data.helpers;
using network.managers;

namespace network.common.data
{
    public static class GameCorruptionTextData
    {
        private static Dictionary<CreepyType, List<LocalizedText>> _textsByType = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            _textsByType.Clear();

            foreach (CreepyType type in Enum.GetValues(typeof(CreepyType)))
            {
                _textsByType[type] = new List<LocalizedText>();
            }

            foreach (var row in csvData)
            {
                if (!row.ContainsKey("text") || !row.ContainsKey("type")) continue;

                if (int.TryParse(row["type"], out int typeValue))
                {
                    var creepyType = (CreepyType)typeValue;
                    if (_textsByType.ContainsKey(creepyType))
                    {
                        _textsByType[creepyType].Add(LocalizedText.FromCsv(row, "text"));
                    }
                }
            }
        }

        public static List<string> GetTexts(CreepyType type)
        {
            return _textsByType.TryGetValue(type, out var texts)
                ? texts.Select(t => t.Kr).ToList()
                : new List<string>();
        }

        public static void Validate(LogManager logManager)
        {
            LogManager.WriteInfoLog("=== GameCorruptionTextData Validation ===");

            var errors = new List<string>();
            var totalCount = 0;

            foreach (var kvp in _textsByType)
            {
                LogManager.WriteInfoLog($"  CreepyType.{kvp.Key}: {kvp.Value.Count} texts");
                totalCount += kvp.Value.Count;
            }

            LogManager.WriteInfoLog($"Total corruption texts: {totalCount}");

            if (totalCount == 0)
            {
                errors.Add("No corruption texts loaded");
            }

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
            LogManager.WriteInfoLog("");
        }
    }
}
