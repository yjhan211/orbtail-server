// ReSharper disable All

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using network.common.data.helpers;
using network.managers;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#endif

namespace network.common.data.helpers
{
    public static class GameDataHelper
    {
        // 서버 환경에서 CSV 파일 기본 경로 (SetBasePath로 설정 가능)
        private static string _basePath = "";

        private static class DataFiles
        {
            public const string GameRule = "game_rule.csv";
            public const string LoadingText = "loading_text.csv";
            public const string AreaName = "area_name.csv";
            public const string AreaRule = "area_rule.csv";
            public const string SystemText = "system_text.csv";
            public const string PortalCondition = "portal_condition.csv";

            public static class Interactable
            {
                public const string Info = "interactable_info.csv";
                public const string Action = "interactable_action.csv";
                public const string Reward = "interactable_reward.csv";

                public static readonly string[] ALL = new[] { Info, Action, Reward };
            }

            public static class Exit
            {
                public const string Template = "exit_template.csv";
                public const string Step = "exit_step.csv";
                public const string Item = "exit_item.csv";
                public const string Spot = "exit_spot.csv";
                public const string Debuff = "exit_debuff.csv";
                public const string Condition = "exit_condition.csv";
                public const string Constraint = "exit_constraint.csv";
                public const string Scenario = "exit_scenario.csv";

                public static readonly string[] ALL = new[] { Template, Step, Item, Spot, Debuff, Condition, Constraint, Scenario };
            }

            public static class Item
            {
                public const string Base = "item_info.csv";
                public const string Consumable = "item_info_consumable.csv";

                public static readonly string[] ALL = new[] { Base, Consumable };
            }

            public static class Map
            {
                public const string MapInfo = "map_info.csv";
                public const string MapRegion = "map_region.csv";

                public static readonly string[] ALL = new[] { MapInfo, MapRegion };
            }
        }

        private static readonly (string fileName, Action<List<CsvRow>> init, Action<LogManager> validate)[]
            StandardDataDefinitions =
            {
                (fileName: DataFiles.GameRule, init: GameRuleData.Initialize, validate: GameRuleData.Validate),
                (fileName: DataFiles.LoadingText, init: GameLoadingTextData.Initialize, validate: GameLoadingTextData.Validate),
                (fileName: DataFiles.AreaName, init: GameAreaNameData.Initialize, validate: GameAreaNameData.Validate),
                (fileName: DataFiles.AreaRule, init: GameAreaRuleData.Initialize, validate: GameAreaRuleData.Validate),
                (fileName: DataFiles.SystemText, init: GameSystemTextData.Initialize, validate: null),
                (fileName: DataFiles.PortalCondition, init: GamePortalConditionData.Initialize, validate: null),
            };

        /// <summary>
        /// 서버 환경에서 CSV 파일 경로의 기본 디렉토리 설정
        /// Unity 환경에서는 호출할 필요 없음
        /// </summary>
        public static void SetBasePath(string basePath)
        {
            _basePath = basePath;
        }

        private static string GetCsvFilePath(string fileName)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // 안드로이드: Resources 폴더 사용 (확장자 제거)
            return $"Common/csv/{System.IO.Path.GetFileNameWithoutExtension(fileName)}";
#elif UNITY_5_3_OR_NEWER
            // Unity (iOS, 윈도우, 에디터): StreamingAssets 경로 사용
            return Path.Combine(Application.streamingAssetsPath, "Common", "csv", fileName);
#else
            // 서버 환경: 설정된 기본 경로 사용
            return Path.Combine(_basePath, "Common", "csv", fileName);
#endif
        }

        private static void Log(string message)
        {
#if UNITY_5_3_OR_NEWER
            Debug.Log(message);
#else
            Console.WriteLine(message);
#endif
        }

        private static void LogError(string message)
        {
#if UNITY_5_3_OR_NEWER
            Debug.LogError(message);
#else
            Console.Error.WriteLine(message);
#endif
        }

        public static void Initialize()
        {
            Log("[GameDataHelper] Initialize started");

            // 모든 CSV 데이터 로드
            var loadedData = new Dictionary<string, List<CsvRow>>();

            // StandardDataDefinitions 파일 로드
            foreach (var (fileName, init, _) in StandardDataDefinitions)
            {
                var filePath = GetCsvFilePath(fileName);
                Log($"[GameDataHelper] Loading: {filePath}");
                try
                {
                    loadedData[fileName] = CsvHelper.LoadCsv(filePath);
                    Log($"[GameDataHelper] Loaded {fileName}: {loadedData[fileName].Count} rows");
                }
                catch (Exception ex)
                {
                    LogError($"[GameDataHelper] Failed to load {fileName}: {ex.Message}");
                    throw;
                }
            }

            // 아이템 관련 파일 로드
            foreach (var fileName in DataFiles.Item.ALL)
            {
                var filePath = GetCsvFilePath(fileName);
                try
                {
                    loadedData[fileName] = CsvHelper.LoadCsv(filePath);
                    Log($"[GameDataHelper] Loaded {fileName}: {loadedData[fileName].Count} rows");
                }
                catch (Exception ex)
                {
                    LogError($"[GameDataHelper] Failed to load {fileName}: {ex.Message}");
                    throw;
                }
            }

            // 맵 관련 파일 로드
            foreach (var fileName in DataFiles.Map.ALL)
            {
                var filePath = GetCsvFilePath(fileName);
                try
                {
                    loadedData[fileName] = CsvHelper.LoadCsv(filePath);
                    Log($"[GameDataHelper] Loaded {fileName}: {loadedData[fileName].Count} rows");
                }
                catch (Exception ex)
                {
                    LogError($"[GameDataHelper] Failed to load {fileName}: {ex.Message}");
                    throw;
                }
            }

            // 상호작용 오브젝트 관련 파일 로드
            foreach (var fileName in DataFiles.Interactable.ALL)
            {
                var filePath = GetCsvFilePath(fileName);
                try
                {
                    loadedData[fileName] = CsvHelper.LoadCsv(filePath);
                    Log($"[GameDataHelper] Loaded {fileName}: {loadedData[fileName].Count} rows");
                }
                catch (Exception ex)
                {
                    LogError($"[GameDataHelper] Failed to load {fileName}: {ex.Message}");
                    throw;
                }
            }

            // 탈출 의식 관련 파일 로드
            foreach (var fileName in DataFiles.Exit.ALL)
            {
                var filePath = GetCsvFilePath(fileName);
                try
                {
                    loadedData[fileName] = CsvHelper.LoadCsv(filePath);
                    Log($"[GameDataHelper] Loaded {fileName}: {loadedData[fileName].Count} rows");
                }
                catch (Exception ex)
                {
                    LogError($"[GameDataHelper] Failed to load {fileName}: {ex.Message}");
                    throw;
                }
            }

            // 일반 데이터 초기화
            foreach (var (fileName, init, _) in StandardDataDefinitions)
                try
                {
                    init(loadedData[fileName]);
                }
                catch (Exception)
                {
                    throw;
                }

            // 아이템 데이터 초기화
            GameItemData.Initialize(
                loadedData[DataFiles.Item.Base],
                new List<CsvRow>(), // Equipment (미사용)
                loadedData[DataFiles.Item.Consumable],
                new List<CsvRow>()  // Put (미사용)
            );

            // 맵 데이터 초기화
            GameMapData.Initialize(
                loadedData[DataFiles.Map.MapInfo],
                loadedData[DataFiles.Map.MapRegion]
            );

            // 상호작용 오브젝트 데이터 초기화
            GameInteractableData.Initialize(
                loadedData[DataFiles.Interactable.Info],
                loadedData[DataFiles.Interactable.Action],
                loadedData[DataFiles.Interactable.Reward]
            );

            // 탈출 의식 데이터 초기화
            GameExitData.Initialize(
                loadedData[DataFiles.Exit.Template],
                loadedData[DataFiles.Exit.Step],
                loadedData[DataFiles.Exit.Item],
                loadedData[DataFiles.Exit.Spot],
                loadedData[DataFiles.Exit.Debuff],
                loadedData[DataFiles.Exit.Condition],
                loadedData[DataFiles.Exit.Constraint]
            );

            // 탈출 시나리오 데이터 초기화
            GameExitScenarioData.Initialize(loadedData[DataFiles.Exit.Scenario]);

            ValidateAllData();
        }

        private static void ValidateAllData()
        {
            try
            {
                GameMapData.Validate();
            }
            catch (Exception)
            {
                throw;
            }
        }
    }
}
