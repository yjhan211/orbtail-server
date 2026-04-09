// ReSharper disable All

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
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

        private static readonly (string fileName, Action<List<CsvRow>> init, Action<LogManager> validate)[]
            _standardDataDefinitions =
            {
                (fileName: DataFiles.GameRule, init: GameRuleData.Initialize, validate: GameRuleData.Validate),
                (fileName: DataFiles.LoadingText, init: GameLoadingTextData.Initialize,
                    validate: GameLoadingTextData.Validate),
                (fileName: DataFiles.AreaName, init: GameAreaNameData.Initialize,
                    validate: GameAreaNameData.Validate),
                (fileName: DataFiles.AreaRule, init: GameAreaRuleData.Initialize,
                    validate: GameAreaRuleData.Validate),
                (fileName: DataFiles.SystemText, init: GameSystemTextData.Initialize, validate: null),
                (fileName: DataFiles.DoorInfo, init: GameDoorData.Initialize, validate: null),
                (fileName: DataFiles.CorruptionText, init: GameCorruptionTextData.Initialize,
                    validate: GameCorruptionTextData.Validate),
                (fileName: DataFiles.ErrorMessage, init: GameErrorMessageData.Initialize, validate: null)
            };

        /// <summary>
        ///     서버 환경에서 CSV 파일 경로의 기본 디렉토리 설정
        ///     Unity 환경에서는 호출할 필요 없음
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
            LogManager.WriteDebugLog(message);
#endif
        }

        private static void LogError(string message)
        {
#if UNITY_5_3_OR_NEWER
            Debug.LogError(message);
#else
            LogManager.WriteErrorLog(message);
#endif
        }

        public static void Initialize()
        {
            Log("[GameDataHelper] Initialize started");

            // 모든 CSV 데이터 로드
            var loadedData = new Dictionary<string, List<CsvRow>>();

            // _standardDataDefinitions 파일 로드
            foreach (var (fileName, init, _) in _standardDataDefinitions)
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

            // 버프 정보 로드
            {
                var filePath = GetCsvFilePath(DataFiles.BuffInfo);
                try
                {
                    loadedData[DataFiles.BuffInfo] = CsvHelper.LoadCsv(filePath);
                    Log($"[GameDataHelper] Loaded {DataFiles.BuffInfo}: {loadedData[DataFiles.BuffInfo].Count} rows");
                }
                catch (Exception ex)
                {
                    LogError($"[GameDataHelper] Failed to load {DataFiles.BuffInfo}: {ex.Message}");
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

            // 미션 관련 파일 로드
            foreach (var fileName in DataFiles.Mission.ALL)
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
            foreach (var (fileName, init, _) in _standardDataDefinitions)
                try
                {
                    init(loadedData[fileName]);
                }
                catch (Exception)
                {
                    throw;
                }

            // 버프 데이터 초기화
            GameBuffData.Initialize(loadedData[DataFiles.BuffInfo]);

            // 아이템 데이터 초기화
            GameItemData.Initialize(
                loadedData[DataFiles.Item.Base],
                loadedData[DataFiles.Item.Equipment],
                loadedData[DataFiles.Item.Consumable],
                new List<CsvRow>() // Put (미사용)
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
                loadedData[DataFiles.Interactable.Violation],
                loadedData[DataFiles.Interactable.ItemPool]
            );

            // 탈출 의식 데이터 초기화
            GameExitData.Initialize(loadedData[DataFiles.Exit.Step]);

            // 미션 데이터 초기화
            GameMissionData.Initialize(loadedData[DataFiles.Mission.Step]);

            ValidateAllData();
        }

        private static void ValidateAllData()
        {
            GameMapData.Validate();
            ValidateReferentialIntegrity();
        }

        /// <summary>
        ///     CSV 간 참조 무결성 검증
        /// </summary>
        private static void ValidateReferentialIntegrity()
        {
            Log("[GameDataHelper] Validating referential integrity...");

            var errors = new List<string>();
            var itemIds = new HashSet<int>(GameItemData.GetAllList().Select(i => i.Id));
            var poolIds = GameInteractableData.GetAllItemPoolIds();

            // 1. interactable_action result_type=1(REWARD_POOL) → interactable_item_pool id 존재
            foreach (var info in GameInteractableData.GetAll())
            {
                foreach (var action in info.Actions)
                {
                    if (action.ResultType == ActionResultType.REWARD_POOL && !poolIds.Contains(action.ResultId))
                    {
                        errors.Add(
                            $"interactable_action [{info.Id}_{action.ActionId}]: result_id={action.ResultId}이 item_pool에 없음");
                    }

                    // 5. interactable_action require_item_id (≠0) → item_info id 존재
                    if (action.RequireItemId != 0 && !itemIds.Contains(action.RequireItemId))
                    {
                        errors.Add(
                            $"interactable_action [{info.Id}_{action.ActionId}]: require_item_id={action.RequireItemId}이 item_info에 없음");
                    }
                }
            }

            // 2. door_info required_item_id → item_info id 존재
            foreach (var door in GameDoorData.GetAll())
            {
                if (door.RequiredItemId != 0 && !itemIds.Contains(door.RequiredItemId))
                {
                    errors.Add($"door_info [{door.DoorId}]: required_item_id={door.RequiredItemId}이 item_info에 없음");
                }
            }

            // 3, 4. exit_step 검증
            foreach (var (groupId, steps) in GameExitData.GetAllStepGroups())
            {
                foreach (var step in steps)
                {
                    // 3. target_item_id → item_info id 존재
                    foreach (var targetItemId in step.TargetItemIds)
                    {
                        if (!itemIds.Contains(targetItemId))
                        {
                            errors.Add(
                                $"exit_step [group={groupId}, order={step.StepOrder}]: target_item_id={targetItemId}이 item_info에 없음");
                        }
                    }

                    // 4. target_interactable_action → interactable_info + interactable_action 유효 조합
                    foreach (var actionKey in step.TargetInteractableActions)
                    {
                        var parts = actionKey.Split('_');
                        if (parts.Length != 2) continue;

                        if (int.TryParse(parts[0], out var interactId) && int.TryParse(parts[1], out var actionId))
                        {
                            var interactable = GameInteractableData.Get(interactId);
                            if (interactable == null)
                            {
                                errors.Add(
                                    $"exit_step [group={groupId}, order={step.StepOrder}]: interactable {interactId}이 interactable_info에 없음");
                            }
                            else if (!interactable.Actions.Any(a => a.ActionId == actionId))
                            {
                                errors.Add(
                                    $"exit_step [group={groupId}, order={step.StepOrder}]: action {interactId}_{actionId}이 interactable_action에 없음");
                            }
                        }
                    }
                }
            }

            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    LogError($"[Integrity] {error}");
                }

                throw new InvalidDataException($"참조 무결성 검증 실패: {errors.Count}건\n{string.Join("\n", errors)}");
            }

            Log($"[GameDataHelper] Referential integrity validation passed!");
        }

        private static class DataFiles
        {
            public const string GameRule = "game_rule.csv";
            public const string LoadingText = "loading_text.csv";
            public const string AreaName = "area_name.csv";
            public const string AreaRule = "area_rule.csv";
            public const string SystemText = "system_text.csv";
            public const string DoorInfo = "door_info.csv";
            public const string CorruptionText = "corruption_text.csv";
            public const string ErrorMessage = "error_message.csv";

            public const string BuffInfo = "buff_info.csv";

            public static class Interactable
            {
                public const string Info = "interactable_info.csv";
                public const string Action = "interactable_action.csv";
                public const string Violation = "interactable_action_violation.csv";
                public const string ItemPool = "interactable_item_pool.csv";

                public static readonly string[] ALL = new[] { Info, Action, Violation, ItemPool };
            }

            public static class Exit
            {
                public const string Step = "exit_step.csv";

                public static readonly string[] ALL = new[] { Step };
            }

            public static class Mission
            {
                public const string Step = "mission_step.csv";

                public static readonly string[] ALL = new[] { Step };
            }

            public static class Item
            {
                public const string Base = "item_info.csv";
                public const string Equipment = "item_info_equipment.csv";
                public const string Consumable = "item_info_consumable.csv";

                public static readonly string[] ALL = new[] { Base, Equipment, Consumable };
            }

            public static class Map
            {
                public const string MapInfo = "map_info.csv";
                public const string MapRegion = "map_region.csv";

                public static readonly string[] ALL = new[] { MapInfo, MapRegion };
            }
        }
    }
}
