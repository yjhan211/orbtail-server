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
                (fileName: DataFiles.AreaConnection, init: GameAreaConnectionData.Initialize, validate: null),
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
                loadedData[DataFiles.Interactable.ItemPool]
            );
            GameInteractableData.InitializeAreaItemPool(loadedData[DataFiles.Interactable.AreaItemPool]);

            // 미션 데이터 초기화 (v0.2.0 — 부품 결합 시스템)
            GameMissionData.Initialize(loadedData[DataFiles.Mission.Step]);
            PartRecipeData.Initialize(loadedData[DataFiles.Mission.PartRecipe]);
            PrerequisiteItemData.Initialize(loadedData[DataFiles.Mission.PrerequisiteItem]);
            GameMissionGraphData.Initialize(
                loadedData[DataFiles.Mission.GraphNode],
                loadedData[DataFiles.Mission.GraphRecipe]);

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

            ValidateMissionReferentialIntegrity(errors, itemIds);

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

        private static void ValidateMissionReferentialIntegrity(List<string> errors, HashSet<int> itemIds)
        {
            var partIds = new HashSet<int>(GameMissionData.GetAllParts().Select(part => part.PartId));
            var interactables = GameInteractableData.GetAll();

            foreach (var part in GameMissionData.GetAllParts())
            {
                if (!Enum.IsDefined(typeof(PartTier), part.PartTier))
                    errors.Add($"mission_step [{part.PartId}]: part_tier={part.PartTier} invalid");

                if (part.SpriteItemId > 0 && !itemIds.Contains(part.SpriteItemId))
                    errors.Add($"mission_step [{part.PartId}]: sprite_item_id={part.SpriteItemId} not found in item_info");

                if (part.TargetArea > 0 && !Enum.IsDefined(typeof(AreaType), part.TargetArea))
                    errors.Add($"mission_step [{part.PartId}]: target_area={part.TargetArea} invalid");

                if (part.TargetObjectType > 0 && !Enum.IsDefined(typeof(InteractableObjectType), part.TargetObjectType))
                    errors.Add($"mission_step [{part.PartId}]: target_object_type={part.TargetObjectType} invalid");

                if (part.TargetArea > 0 && part.TargetObjectType > 0)
                {
                    bool hasTarget = interactables.Any(info =>
                        info.ZoneId == part.TargetArea && (int)info.ObjectType == part.TargetObjectType);
                    if (!hasTarget)
                    {
                        errors.Add(
                            $"mission_step [{part.PartId}]: no interactable for area={part.TargetArea}, object_type={part.TargetObjectType}");
                    }
                }
            }

            foreach (var recipe in PartRecipeData.GetAllRecipes())
            {
                var inputA = GameMissionData.GetPart(recipe.InputPartA);
                var inputB = GameMissionData.GetPart(recipe.InputPartB);
                var output = GameMissionData.GetPart(recipe.OutputPart);

                if (inputA == null)
                    errors.Add($"part_recipe [{recipe.Id}]: input_part_a={recipe.InputPartA} not found in mission_step");
                if (inputB == null)
                    errors.Add($"part_recipe [{recipe.Id}]: input_part_b={recipe.InputPartB} not found in mission_step");
                if (output == null)
                    errors.Add($"part_recipe [{recipe.Id}]: output_part={recipe.OutputPart} not found in mission_step");

                if (inputA != null && inputA.JobTitle != recipe.JobTitle)
                    errors.Add($"part_recipe [{recipe.Id}]: input_part_a job mismatch ({inputA.JobTitle} != {recipe.JobTitle})");
                if (inputB != null && inputB.JobTitle != recipe.JobTitle)
                    errors.Add($"part_recipe [{recipe.Id}]: input_part_b job mismatch ({inputB.JobTitle} != {recipe.JobTitle})");
                if (output != null && output.JobTitle != recipe.JobTitle)
                    errors.Add($"part_recipe [{recipe.Id}]: output_part job mismatch ({output.JobTitle} != {recipe.JobTitle})");
            }

            foreach (var prerequisite in PrerequisiteItemData.GetAll())
            {
                if (!partIds.Contains(prerequisite.TargetPartId))
                {
                    errors.Add(
                        $"prerequisite_item [target_part_id={prerequisite.TargetPartId}]: target part not found in mission_step");
                }

                if (prerequisite.LocationArea > 0 && !Enum.IsDefined(typeof(AreaType), prerequisite.LocationArea))
                    errors.Add(
                        $"prerequisite_item [target_part_id={prerequisite.TargetPartId}]: location_area={prerequisite.LocationArea} invalid");

                if (prerequisite.LocationObjectType > 0 &&
                    !Enum.IsDefined(typeof(InteractableObjectType), prerequisite.LocationObjectType))
                    errors.Add(
                        $"prerequisite_item [target_part_id={prerequisite.TargetPartId}]: location_object_type={prerequisite.LocationObjectType} invalid");

                if (prerequisite.LocationArea > 0 && prerequisite.LocationObjectType > 0)
                {
                    bool hasTarget = interactables.Any(info =>
                        info.ZoneId == prerequisite.LocationArea &&
                        (int)info.ObjectType == prerequisite.LocationObjectType);
                    if (!hasTarget)
                    {
                        errors.Add(
                            $"prerequisite_item [target_part_id={prerequisite.TargetPartId}]: no interactable for area={prerequisite.LocationArea}, object_type={prerequisite.LocationObjectType}");
                    }
                }

                if (prerequisite.SpriteItemId > 0 && !itemIds.Contains(prerequisite.SpriteItemId))
                {
                    errors.Add(
                        $"prerequisite_item [target_part_id={prerequisite.TargetPartId}]: sprite_item_id={prerequisite.SpriteItemId} not found in item_info");
                }
            }

            foreach (var node in GameMissionGraphData.GetAllNodes())
            {
                bool isSharedStoryletNode = node.JobTitle == 0 && node.HasStoryletMetadata;

                if (!isSharedStoryletNode && !Enum.IsDefined(typeof(JobTitle), (JobTitle)node.JobTitle))
                    errors.Add($"mission_graph_node [{node.NodeId}]: job_title={node.JobTitle} invalid");

                if (!Enum.IsDefined(typeof(MissionGraphNodeKind), node.NodeKind))
                    errors.Add($"mission_graph_node [{node.NodeId}]: node_kind={node.NodeKind} invalid");

                if (node.AreaType > 0 && !Enum.IsDefined(typeof(AreaType), (AreaType)node.AreaType))
                    errors.Add($"mission_graph_node [{node.NodeId}]: area_type={node.AreaType} invalid");

                if (node.ObjectType > 0 && !Enum.IsDefined(typeof(InteractableObjectType), (InteractableObjectType)node.ObjectType))
                    errors.Add($"mission_graph_node [{node.NodeId}]: object_type={node.ObjectType} invalid");

                if (node.InteractId > 0)
                {
                    var interactable = interactables.FirstOrDefault(info => info.Id == node.InteractId);
                    if (interactable == null)
                    {
                        errors.Add($"mission_graph_node [{node.NodeId}]: interact_id={node.InteractId} not found");
                    }
                    else
                    {
                        if (node.AreaType > 0 && interactable.ZoneId != node.AreaType)
                            errors.Add(
                                $"mission_graph_node [{node.NodeId}]: interact_id={node.InteractId} area mismatch ({interactable.ZoneId} != {node.AreaType})");

                        if (node.ObjectType > 0 && (int)interactable.ObjectType != node.ObjectType)
                            errors.Add(
                                $"mission_graph_node [{node.NodeId}]: interact_id={node.InteractId} object mismatch ({(int)interactable.ObjectType} != {node.ObjectType})");
                    }
                }
                else if (node.AreaType > 0 && node.ObjectType > 0)
                {
                    bool hasTarget = interactables.Any(info =>
                        info.ZoneId == node.AreaType && (int)info.ObjectType == node.ObjectType);
                    if (!hasTarget)
                    {
                        errors.Add(
                            $"mission_graph_node [{node.NodeId}]: no interactable for area={node.AreaType}, object_type={node.ObjectType}");
                    }
                }

                foreach (var partId in node.RequiredPartIds)
                {
                    var part = GameMissionData.GetPart(partId);
                    if (part == null)
                        errors.Add($"mission_graph_node [{node.NodeId}]: required_part_id={partId} not found");
                    else if (!isSharedStoryletNode && part.JobTitle != node.JobTitle)
                        errors.Add($"mission_graph_node [{node.NodeId}]: required_part_id={partId} job mismatch");
                }

                foreach (var itemId in node.RequiredItemIds)
                {
                    if (!itemIds.Contains(itemId))
                        errors.Add($"mission_graph_node [{node.NodeId}]: required_item_id={itemId} not found in item_info");
                }

                foreach (var requiredNodeId in node.RequiredNodeIds)
                {
                    var requiredNode = GameMissionGraphData.GetNode(requiredNodeId);
                    if (requiredNode == null)
                        errors.Add($"mission_graph_node [{node.NodeId}]: required_node_id={requiredNodeId} not found");
                    else if (!isSharedStoryletNode && requiredNode.JobTitle != node.JobTitle)
                        errors.Add($"mission_graph_node [{node.NodeId}]: required_node_id={requiredNodeId} job mismatch");
                }

                foreach (var unlockNodeId in node.UnlockNodeIds)
                {
                    var unlockNode = GameMissionGraphData.GetNode(unlockNodeId);
                    if (unlockNode == null)
                        errors.Add($"mission_graph_node [{node.NodeId}]: unlock_node_id={unlockNodeId} not found");
                    else if (!isSharedStoryletNode && unlockNode.JobTitle != node.JobTitle)
                        errors.Add($"mission_graph_node [{node.NodeId}]: unlock_node_id={unlockNodeId} job mismatch");
                }

                if (node.OutputPartId > 0)
                {
                    var outputPart = GameMissionData.GetPart(node.OutputPartId);
                    if (outputPart == null)
                        errors.Add($"mission_graph_node [{node.NodeId}]: output_part_id={node.OutputPartId} not found");
                    else if (!isSharedStoryletNode && outputPart.JobTitle != node.JobTitle)
                        errors.Add($"mission_graph_node [{node.NodeId}]: output_part_id={node.OutputPartId} job mismatch");
                }

                if (node.TargetAreaType > 0 && !Enum.IsDefined(typeof(AreaType), (AreaType)node.TargetAreaType))
                    errors.Add($"mission_graph_node [{node.NodeId}]: target_area_type={node.TargetAreaType} invalid");

                if (node.TargetObjectType > 0 &&
                    !Enum.IsDefined(typeof(InteractableObjectType), (InteractableObjectType)node.TargetObjectType))
                    errors.Add($"mission_graph_node [{node.NodeId}]: target_object_type={node.TargetObjectType} invalid");

                if (node.TargetAreaType > 0 && node.TargetObjectType > 0)
                {
                    bool hasTarget = interactables.Any(info =>
                        info.ZoneId == node.TargetAreaType && (int)info.ObjectType == node.TargetObjectType);
                    if (!hasTarget)
                    {
                        errors.Add(
                            $"mission_graph_node [{node.NodeId}]: no target interactable for target_area={node.TargetAreaType}, target_object_type={node.TargetObjectType}");
                    }
                }

                if (node.RequiredOutputItemId > 0 &&
                    GameMissionData.GetPart(node.RequiredOutputItemId) == null &&
                    !itemIds.Contains(node.RequiredOutputItemId))
                {
                    errors.Add(
                        $"mission_graph_node [{node.NodeId}]: required_output_item_id={node.RequiredOutputItemId} not found in mission_step or item_info");
                }

                if (node.RecipeId > 0 && GameMissionGraphData.GetRecipe(node.RecipeId) == null)
                    errors.Add($"mission_graph_node [{node.NodeId}]: recipe_id={node.RecipeId} not found");
            }

            foreach (var recipe in GameMissionGraphData.GetAllRecipes())
            {
                if (!Enum.IsDefined(typeof(JobTitle), (JobTitle)recipe.JobTitle))
                    errors.Add($"mission_graph_recipe [{recipe.RecipeId}]: job_title={recipe.JobTitle} invalid");

                if (recipe.InputPartIds.Count < 2)
                    errors.Add($"mission_graph_recipe [{recipe.RecipeId}]: input_part_ids must contain at least 2 parts");

                foreach (var inputPartId in recipe.InputPartIds)
                {
                    var inputPart = GameMissionData.GetPart(inputPartId);
                    if (inputPart == null)
                        errors.Add($"mission_graph_recipe [{recipe.RecipeId}]: input_part_id={inputPartId} not found");
                    else if (inputPart.JobTitle != recipe.JobTitle)
                        errors.Add($"mission_graph_recipe [{recipe.RecipeId}]: input_part_id={inputPartId} job mismatch");
                }

                if (recipe.OutputPartId > 0)
                {
                    var outputPart = GameMissionData.GetPart(recipe.OutputPartId);
                    if (outputPart == null)
                        errors.Add($"mission_graph_recipe [{recipe.RecipeId}]: output_part_id={recipe.OutputPartId} not found");
                    else if (outputPart.JobTitle != recipe.JobTitle)
                        errors.Add($"mission_graph_recipe [{recipe.RecipeId}]: output_part_id={recipe.OutputPartId} job mismatch");
                }

                foreach (var requiredNodeId in recipe.RequiredNodeIds)
                {
                    var requiredNode = GameMissionGraphData.GetNode(requiredNodeId);
                    if (requiredNode == null)
                        errors.Add($"mission_graph_recipe [{recipe.RecipeId}]: required_node_id={requiredNodeId} not found");
                    else if (requiredNode.JobTitle != recipe.JobTitle)
                        errors.Add($"mission_graph_recipe [{recipe.RecipeId}]: required_node_id={requiredNodeId} job mismatch");
                }

                foreach (var unlockNodeId in recipe.UnlockNodeIds)
                {
                    var unlockNode = GameMissionGraphData.GetNode(unlockNodeId);
                    if (unlockNode == null)
                        errors.Add($"mission_graph_recipe [{recipe.RecipeId}]: unlock_node_id={unlockNodeId} not found");
                    else if (unlockNode.JobTitle != recipe.JobTitle)
                        errors.Add($"mission_graph_recipe [{recipe.RecipeId}]: unlock_node_id={unlockNodeId} job mismatch");
                }
            }
        }

        private static class DataFiles
        {
            public const string GameRule = "game_rule.csv";
            public const string LoadingText = "loading_text.csv";
            public const string AreaName = "area_name.csv";
            public const string AreaRule = "area_rule.csv";
            public const string SystemText = "system_text.csv";
            public const string DoorInfo = "door_info.csv";
            public const string AreaConnection = "map_connections.csv";
            public const string CorruptionText = "corruption_text.csv";
            public const string ErrorMessage = "error_message.csv";

            public const string BuffInfo = "buff_info.csv";

            public static class Interactable
            {
                public const string Info = "interactable_info.csv";
                public const string Action = "object_action.csv";  // GDD §2.4.2 — object_type 기반 통합 풀
                public const string ItemPool = "interactable_item_pool.csv";
                public const string AreaItemPool = "area_item_pool.csv";  // #135 — 영역 단위 ItemPool

                public static readonly string[] ALL = new[] { Info, Action, ItemPool, AreaItemPool };
            }

            public static class Mission
            {
                public const string Step = "mission_step.csv";
                public const string PartRecipe = "part_recipe.csv";
                public const string PrerequisiteItem = "prerequisite_item.csv";
                public const string GraphNode = "mission_graph_node.csv";
                public const string GraphRecipe = "mission_graph_recipe.csv";

                public static readonly string[] ALL = new[] { Step, PartRecipe, PrerequisiteItem, GraphNode, GraphRecipe };
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
