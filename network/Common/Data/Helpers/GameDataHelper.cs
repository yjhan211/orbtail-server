// ReSharper disable All

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using network.common.data.helpers;
using network.managers;
using UnityEngine;

namespace network.common.data.helpers
{
    public static class GameDataHelper
    {
        private static class DataFiles
        {
            public const string GameRule = "game_rule.csv";
            public const string BuffInfo = "buff_info.csv";
            public const string QuestInfo = "quest_info.csv";
            public const string MailInfo = "mail_info.csv";
            public const string ExploreTargetInfo = "explore_target_info.csv";
            public const string CraftInfo = "craft_info.csv";
            public const string LoadingText = "loading_text.csv";
            public const string AreaName = "area_name.csv";
            public const string AreaRule = "area_rule.csv";

            public static class Interactable
            {
                public const string Info = "interactable_info.csv";
                public const string Action = "interactable_action.csv";

                public static readonly string[] ALL = new[] { Info, Action };
            }

            public static class Item
            {
                public const string Base = "item_info.csv";
                public const string Equipment = "item_info_equipment.csv";
                public const string Consumable = "item_info_consumable.csv";
                public const string Installation = "item_info_installation.csv";
                public const string Shop = "shop_info_installation.csv";
                public const string Put = "item_info_put.csv";

                public static readonly string[] ALL = new[] { Base, Equipment, Consumable, Installation, Shop, Put };
            }

            public static class Map
            {
                public const string MapInfo = "map_info.csv";
                public const string MapRegion = "map_region.csv";

                public static readonly string[] ALL = new[] { MapInfo, MapRegion };
            }
        }

        private static readonly string NetworkPath = Path.GetDirectoryName(typeof(GameDataHelper).Assembly.Location)!;
        private static readonly (string fileName, Action<List<CsvRow>> init, Action<LogManager> validate)[]
            StandardDataDefinitions =
            {
                (fileName: DataFiles.GameRule, init: GameRuleData.Initialize, validate: GameRuleData.Validate),
                (fileName: DataFiles.BuffInfo, init: GameBuffData.Initialize, validate: GameBuffData.Validate),
                (fileName: DataFiles.QuestInfo, init: GameQuestData.Initialize, validate: GameQuestData.Validate),
                (fileName: DataFiles.MailInfo, init: GameMailData.Initialize, validate: GameMailData.Validate),
                (fileName: DataFiles.ExploreTargetInfo, init: GameExploreTargetData.Initialize, GameExploreTargetData.Validate),
                (fileName: DataFiles.CraftInfo, init: GameCraftData.Initialize, GameCraftData.Validate),
                (fileName: DataFiles.LoadingText, init: GameLoadingTextData.Initialize, validate: GameLoadingTextData.Validate),
                (fileName: DataFiles.AreaName, init: GameAreaNameData.Initialize, validate: GameAreaNameData.Validate),
                (fileName: DataFiles.AreaRule, init: GameAreaRuleData.Initialize, validate: GameAreaRuleData.Validate),
            };

        private static string GetCsvFilePath(string fileName)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // 안드로이드: Resources 폴더 사용 (확장자 제거)
            return $"Common/csv/{System.IO.Path.GetFileNameWithoutExtension(fileName)}";
#elif UNITY_EDITOR
            // 에디터: StreamingAssets 경로 사용
            return System.IO.Path.Combine(Application.streamingAssetsPath, "Common", "csv", fileName);
#else
            // 윈도우/기타 플랫폼: StreamingAssets 경로 사용
            return Path.Combine(NetworkPath, "Common", "csv", fileName);
#endif
        }

        public static void Initialize()
        {
            // 모든 CSV 데이터 로드
            var loadedData = new Dictionary<string, List<CsvRow>>();

            // StandardDataDefinitions 파일 로드
            foreach (var (fileName, init, _) in StandardDataDefinitions)
            {
                var filePath = GetCsvFilePath(fileName);
                loadedData[fileName] = CsvHelper.LoadCsv(filePath);
            }

            // 아이템 관련 파일 로드
            foreach (var fileName in DataFiles.Item.ALL)
            {
                var filePath = GetCsvFilePath(fileName);
                loadedData[fileName] = CsvHelper.LoadCsv(filePath);
            }

            // 맵 관련 파일 로드
            foreach (var fileName in DataFiles.Map.ALL)
            {
                var filePath = GetCsvFilePath(fileName);
                loadedData[fileName] = CsvHelper.LoadCsv(filePath);
            }

            // 상호작용 오브젝트 관련 파일 로드
            foreach (var fileName in DataFiles.Interactable.ALL)
            {
                var filePath = GetCsvFilePath(fileName);
                loadedData[fileName] = CsvHelper.LoadCsv(filePath);
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
                loadedData[DataFiles.Item.Equipment],
                loadedData[DataFiles.Item.Consumable],
                loadedData[DataFiles.Item.Installation],
                loadedData[DataFiles.Item.Shop],
                loadedData[DataFiles.Item.Put]
            );

            // 맵 데이터 초기화
            GameMapData.Initialize(
                loadedData[DataFiles.Map.MapInfo],
                loadedData[DataFiles.Map.MapRegion]
            );

            // 상호작용 오브젝트 데이터 초기화
            GameInteractableData.Initialize(
                loadedData[DataFiles.Interactable.Info],
                loadedData[DataFiles.Interactable.Action]
            );

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
