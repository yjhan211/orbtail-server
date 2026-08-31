// ReSharper disable All

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using network.common;
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
        private static bool _initialized;

        private static readonly (string fileName, Action<List<CsvRow>> init, Action<LogManager>? validate)[]
            _standardDataDefinitions =
            {
                // 중앙 로컬라이징 테이블 — 다른 데이터가 {prefix}_key로 참조하므로 가장 먼저 초기화
                (fileName: DataFiles.LocalizationText, init: GameLocalizationData.Initialize, validate: null),
                (fileName: DataFiles.GameRule, init: GameRuleData.Initialize, validate: GameRuleData.Validate),
                (fileName: DataFiles.SwarmConfig, init: SwarmConfigData.Initialize, validate: null),
                (fileName: DataFiles.LoadingText, init: GameLoadingTextData.Initialize,
                    validate: GameLoadingTextData.Validate),
                (fileName: DataFiles.AreaName, init: GameAreaNameData.Initialize,
                    validate: GameAreaNameData.Validate),
                (fileName: DataFiles.SystemText, init: GameSystemTextData.Initialize, validate: null),
                (fileName: DataFiles.StatusEffectInfo, init: GameStatusEffectData.Initialize, validate: null),
                (fileName: DataFiles.DoorInfo, init: GameDoorData.Initialize, validate: null),
                (fileName: DataFiles.AreaConnection, init: GameAreaConnectionData.Initialize, validate: null),
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
            var streamingAssetsPath = Path.Combine(Application.streamingAssetsPath, "Common", "csv", fileName);
#if UNITY_EDITOR
            if (!File.Exists(streamingAssetsPath))
            {
                var sourceCsvPath = Path.Combine(Application.dataPath, "Scripts", "Common", "csv", fileName);
                if (File.Exists(sourceCsvPath))
                    return sourceCsvPath;
            }
#endif
            return streamingAssetsPath;
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
            if (_initialized)
            {
                Log("[GameDataHelper] Initialize skipped (already initialized)");
                return;
            }

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

            foreach (var (fileName, init, _) in _standardDataDefinitions)
            {
                init(loadedData[fileName]);
            }

            // Buff data
            GameBuffData.Initialize(loadedData[DataFiles.BuffInfo]);

            // Item data
            GameItemData.Initialize(
                loadedData[DataFiles.Item.Base],
                loadedData[DataFiles.Item.Equipment],
                loadedData[DataFiles.Item.Consumable],
                new List<CsvRow>() // Put (unused)
            );
            BattleItemRecipeData.Initialize(loadedData[DataFiles.Item.BattleRecipe]);
            BattleItemCombatData.Initialize(loadedData[DataFiles.Item.BattleCombat]);

            // Map data — 신맵(School2) 리전은 별도 파일이라 병합해 넘긴다 (행에 map_id가 있어 안전).
            GameMapData.Initialize(
                loadedData[DataFiles.Map.MapInfo],
                loadedData[DataFiles.Map.MapRegion]
                    .Concat(loadedData[DataFiles.Map.MapRegionSchool2])
                    .ToList()
            );
            GameMonsterCampData.Initialize(loadedData[DataFiles.Map.MonsterCampAnchor]);
            SwarmMonsterData.Initialize(loadedData[DataFiles.Map.SwarmMonster]);

            // Interactable object data
            GameInteractableData.Initialize(
                loadedData[DataFiles.Interactable.Info],
                loadedData[DataFiles.Interactable.Action]
            );
            GameInteractableData.InitializeAreaItemPool(loadedData[DataFiles.Interactable.AreaItemPool]);

            ValidateAllData();
            _initialized = true;
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
            var buffIds = new HashSet<int>(GameBuffData.GetAll().Select(buff => buff.Id));

            foreach (int itemId in GameInteractableData.GetAllAreaItemPoolItems())
            {
                if (!itemIds.Contains(itemId))
                    errors.Add($"area_item_pool: item_id={itemId} not found in item_info");
            }

            foreach (var effect in GameStatusEffectData.GetAll())
            {
                if (effect.BuffId != 0 && !buffIds.Contains(effect.BuffId))
                    errors.Add($"status_effect_info [{effect.Id}]: buff_id={effect.BuffId} not found in buff_info");
            }

            // 1. interactable_action require_item_id (≠0) → item_info id 존재
            foreach (var info in GameInteractableData.GetAll())
            {
                foreach (var action in info.Actions)
                {
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

            BattleItemRecipeData.ValidateReferentialIntegrity(errors, itemIds);

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
            public const string SwarmConfig = "swarm_config.csv";  // #296 — 스웜 밸런스 key/value
            public const string LoadingText = "loading_text.csv";
            public const string AreaName = "area_name.csv";
            public const string SystemText = "system_text.csv";
            public const string StatusEffectInfo = "status_effect_info.csv";
            public const string DoorInfo = "door_info.csv";
            public const string AreaConnection = "map_connections.csv";
            public const string ErrorMessage = "error_message.csv";
            public const string LocalizationText = "localization.csv";

            public const string BuffInfo = "buff_info.csv";

            public static class Interactable
            {
                public const string Info = "interactable_info.csv";
                public const string Action = "object_action.csv";  // GDD §2.4.2 — object_type 기반 통합 풀
                public const string AreaItemPool = "area_item_pool.csv";  // #135 — 영역 단위 ItemPool

                public static readonly string[] ALL = new[] { Info, Action, AreaItemPool };
            }

            public static class Item
            {
                public const string Base = "item_info.csv";
                public const string Equipment = "item_info_equipment.csv";
                public const string Consumable = "item_info_consumable.csv";
                public const string BattleRecipe = "battle_item_recipe.csv";
                public const string BattleCombat = "battle_item_combat.csv";

                public static readonly string[] ALL = new[] { Base, Equipment, Consumable, BattleRecipe, BattleCombat };
            }

            public static class Map
            {
                public const string MapInfo = "map_info.csv";
                public const string MapRegion = "map_region.csv";
                // #272 신맵(School2) 전용 리전 — 씬 내보내기가 기존 School 행을 덮지 않게 파일 분리.
                public const string MapRegionSchool2 = "map_region_school2.csv";
                public const string MonsterCampAnchor = "monster_camp_anchor.csv";
                public const string SwarmMonster = "swarm_monster.csv";

                public static readonly string[] ALL = new[] { MapInfo, MapRegion, MapRegionSchool2, MonsterCampAnchor, SwarmMonster };
            }
        }
    }
}
