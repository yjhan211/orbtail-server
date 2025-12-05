// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;
using network.managers;

namespace network.common.data
{
    public static class GameAreaRuleData
    {
        private static readonly Dictionary<int, AreaRuleInfoData> Rules = new();
        private static readonly Dictionary<AreaType, List<AreaRuleInfoData>> RulesByArea = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            Rules.Clear();
            RulesByArea.Clear();

            foreach (var row in csvData)
            {
                var rule = AreaRuleInfoData.CreateFromData(row);
                Rules[rule.Id] = rule;

                if (!RulesByArea.ContainsKey(rule.AreaType))
                {
                    RulesByArea[rule.AreaType] = new List<AreaRuleInfoData>();
                }
                RulesByArea[rule.AreaType].Add(rule);
            }
        }

        public static AreaRuleInfoData Get(int id)
        {
            return Rules.TryGetValue(id, out var rule) ? rule : null;
        }

        public static List<AreaRuleInfoData> GetByArea(AreaType areaType)
        {
            return RulesByArea.TryGetValue(areaType, out var rules) ? rules : new List<AreaRuleInfoData>();
        }

        /// <summary>
        /// 특정 Area의 규칙들을 랜덤하게 선택
        /// - Category 1 (필수): 같은 group_id 중 하나를 무작위 선택
        /// - Category 2 (일반): group_id가 0이면 개별, 같은 group_id끼리는 하나만 선택
        /// </summary>
        public static List<int> SelectRulesForArea(AreaType areaType, Random random = null)
        {
            random ??= new Random();
            var result = new List<int>();
            var areaRules = GetByArea(areaType);

            if (areaRules.Count == 0) return result;

            // Category 1 (필수) 처리 - 각 group_id 당 하나 선택
            var category1Rules = areaRules.Where(r => r.Category == 1).ToList();
            var category1Groups = category1Rules.GroupBy(r => r.GroupId).ToList();

            foreach (var group in category1Groups)
            {
                var groupList = group.ToList();
                var selected = groupList[random.Next(groupList.Count)];
                result.Add(selected.Id);
            }

            // Category 2 (일반) 처리
            var category2Rules = areaRules.Where(r => r.Category == 2).ToList();

            // group_id가 0인 것들 중에서 하나 랜덤 선택
            var individualRules = category2Rules.Where(r => r.GroupId == 0).ToList();
            if (individualRules.Count > 0)
            {
                var selected = individualRules[random.Next(individualRules.Count)];
                result.Add(selected.Id);
            }

            // group_id가 0이 아닌 것들 - 각 group당 하나 선택
            var category2Groups = category2Rules.Where(r => r.GroupId != 0).GroupBy(r => r.GroupId).ToList();
            foreach (var group in category2Groups)
            {
                var groupList = group.ToList();
                var selected = groupList[random.Next(groupList.Count)];
                result.Add(selected.Id);
            }

            return result;
        }

        /// <summary>
        /// 전체 맵의 모든 Area에 대해 규칙 선택
        /// </summary>
        public static Dictionary<AreaType, List<int>> SelectRulesForAllAreas(Random random = null)
        {
            random ??= new Random();
            var result = new Dictionary<AreaType, List<int>>();

            foreach (var areaType in RulesByArea.Keys)
            {
                result[areaType] = SelectRulesForArea(areaType, random);
            }

            return result;
        }

        public static void Validate(LogManager logManager)
        {
            LogManager.WriteDebugLog("=== GameAreaRuleData Validation ===");
            LogManager.WriteDebugLog($"Total {Rules.Count} rules loaded");

            foreach (var (areaType, rules) in RulesByArea)
            {
                LogManager.WriteDebugLog($"Area {areaType}: {rules.Count} rules");
                var cat1Count = rules.Count(r => r.Category == 1);
                var cat2Count = rules.Count(r => r.Category == 2);
                LogManager.WriteDebugLog($"  Category 1 (필수): {cat1Count}");
                LogManager.WriteDebugLog($"  Category 2 (일반): {cat2Count}");
            }

            LogManager.WriteDebugLog("GameAreaRuleData validation completed!");
        }
    }

    public class AreaRuleInfoData
    {
        public int Id { get; private set; }
        public AreaType AreaType { get; private set; }
        public int Category { get; private set; } // 1=필수, 2=일반
        public int GroupId { get; private set; } // 0=개별, 같은 값끼리 상호배타
        public string Description { get; private set; }

        public static AreaRuleInfoData CreateFromData(CsvRow row)
        {
            return new AreaRuleInfoData
            {
                Id = int.Parse(row["id"]),
                AreaType = (AreaType)int.Parse(row["area_type"]),
                Category = int.Parse(row["category"]),
                GroupId = int.Parse(row["group_id"]),
                Description = row["description"]
            };
        }
    }
}
