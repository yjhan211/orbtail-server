// ReSharper disable All

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using network.managers;

namespace network.common.data.helpers
{
    public static class GameDataHelper
    {
        private static LogManager _logManager = null!;
        private static readonly string NetworkPath = Path.GetDirectoryName(typeof(GameDataHelper).Assembly.Location)!;

        private static readonly (string fileName, Action<Dictionary<string, CsvRow>> init, Action<LogManager> validate)[]
            StandardDataDefinitions = new[]
            {
                ((string fileName, Action<Dictionary<string, CsvRow>> init, Action<LogManager> validate))(DataFiles.GameRule, GameRuleData.Initialize, GameRuleData.Validate),
                ((string fileName, Action<Dictionary<string, CsvRow>> init, Action<LogManager> validate))(DataFiles.BuffInfo, GameBuffData.Initialize, GameBuffData.Validate)
            };

        public static void Initialize(LogManager logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

            // 모든 CSV 데이터 로드
            var loadedData = new Dictionary<string, Dictionary<string, CsvRow>>();

            // 일반 데이터 파일 로드
            foreach (var (fileName, _, _) in StandardDataDefinitions)
            {
                var filePath = Path.Combine(NetworkPath, "Common/csv", fileName);
                loadedData[fileName] = CsvHelper.LoadCsv(filePath);
            }

            // 아이템 관련 파일 로드
            foreach (var fileName in DataFiles.Item.ALL)
            {
                var filePath = Path.Combine(NetworkPath, "Common/csv", fileName);
                loadedData[fileName] = CsvHelper.LoadCsv(filePath);
            }

            // 일반 데이터 초기화
            foreach (var (fileName, init, _) in StandardDataDefinitions)
                try
                {
                    init(loadedData[fileName]);
                }
                catch (Exception ex)
                {
                    _logManager.WriteErrorLog(new Exception($"Failed to initialize {fileName}", ex));
                    throw;
                }

            // 아이템 데이터 초기화
            GameItemData.Initialize(
                loadedData[DataFiles.Item.Base],
                loadedData[DataFiles.Item.Equipment],
                loadedData[DataFiles.Item.Consumable],
                loadedData[DataFiles.Item.Installation],
                loadedData[DataFiles.Item.Shop]
            );

            ValidateAllData();
        }

        private static void ValidateAllData()
        {
            try
            {
                // 일반 데이터 검증
                foreach (var (fileName, _, validate) in StandardDataDefinitions)
                    try
                    {
                        validate(_logManager);
                    }
                    catch (Exception ex)
                    {
                        _logManager.WriteErrorLog(new Exception($"Validation failed for {fileName}", ex));
                        throw;
                    }

                // 아이템 데이터 검증
                GameItemData.Validate(_logManager);

                _logManager.WriteDebugLog("All data validations completed successfully!");
            }
            catch (Exception ex)
            {
                _logManager.WriteErrorLog(new Exception("Data validation failed", ex));
                throw;
            }
        }

        private static class DataFiles
        {
            public const string GameRule = "game_rule.csv";
            public const string BuffInfo = "buff_info.csv";

            public static class Item
            {
                public const string Base = "item_info.csv";
                public const string Equipment = "item_info_equipment.csv";
                public const string Consumable = "item_info_consumable.csv";
                public const string Installation = "item_info_installation.csv";
                public const string Shop = "shop_info_installation.csv";

                public static readonly string[] ALL = new[] { Base, Equipment, Consumable, Installation, Shop };
            }
        }
    }
}