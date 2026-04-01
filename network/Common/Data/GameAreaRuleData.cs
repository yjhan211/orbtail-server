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
        private static readonly Dictionary<int, AreaRuleInfoData> _rules = new();
        private static readonly Dictionary<AreaType, List<AreaRuleInfoData>> _rulesByArea = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            _rules.Clear();
            _rulesByArea.Clear();

            foreach (var row in csvData)
            {
                var rule = AreaRuleInfoData.CreateFromData(row);
                _rules[rule.Id] = rule;

                if (!_rulesByArea.ContainsKey(rule.AreaType))
                {
                    _rulesByArea[rule.AreaType] = new List<AreaRuleInfoData>();
                }
                _rulesByArea[rule.AreaType].Add(rule);
            }
        }

        public static AreaRuleInfoData Get(int id)
        {
            return _rules.TryGetValue(id, out var rule) ? rule : null;
        }

        public static List<AreaRuleInfoData> GetByArea(AreaType areaType)
        {
            return _rulesByArea.TryGetValue(areaType, out var rules) ? rules : new List<AreaRuleInfoData>();
        }

        /// <summary>
        /// 특정 Area의 규칙들을 랜덤하게 선택
        /// - group_id=0: 전체에서 하나만 선택
        /// - group_id!=0: 같은 group_id끼리 하나만 선택
        /// </summary>
        public static List<int> SelectRulesForArea(AreaType areaType, Random random = null)
        {
            random ??= new Random();
            var result = new List<int>();
            var areaRules = GetByArea(areaType);

            if (areaRules.Count == 0) return result;

            // group_id=0인 규칙들 중 하나 랜덤 선택
            var individualRules = areaRules.Where(r => r.GroupId == 0).ToList();
            if (individualRules.Count > 0)
            {
                var selected = individualRules[random.Next(individualRules.Count)];
                result.Add(selected.Id);
            }

            // group_id!=0인 규칙들 - 각 group당 하나 선택
            var groupedRules = areaRules.Where(r => r.GroupId != 0).GroupBy(r => r.GroupId).ToList();
            foreach (var group in groupedRules)
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

            foreach (var areaType in _rulesByArea.Keys)
            {
                result[areaType] = SelectRulesForArea(areaType, random);
            }

            return result;
        }

        public static void Validate(LogManager logManager)
        {
            LogManager.WriteDebugLog("=== GameAreaRuleData Validation ===");
            LogManager.WriteDebugLog($"Total {_rules.Count} rules loaded");

            foreach (var (areaType, rules) in _rulesByArea)
            {
                var group0Count = rules.Count(r => r.GroupId == 0);
                var groupedCount = rules.Count(r => r.GroupId != 0);
                LogManager.WriteDebugLog($"Area {areaType}: {rules.Count} rules (group0: {group0Count}, grouped: {groupedCount})");
            }

            LogManager.WriteDebugLog("GameAreaRuleData validation completed!");
        }
    }

    public class AreaRuleInfoData
    {
        public int Id { get; private set; }
        public AreaType AreaType { get; private set; }
        public int GroupId { get; private set; } // 0=개별(하나만 선택), !=0=같은 값끼리 하나만 선택
        public string Description { get; private set; }
        public int TargetInteractId { get; private set; } // 0=해당없음, >0=해당 오브젝트 탐색 시 위반
        public int TargetActionId { get; private set; } // 0=모든 액션, >0=특정 액션만 위반

        public static AreaRuleInfoData CreateFromData(CsvRow row)
        {
            var targetInteractId = 0;
            if (row.ContainsKey("target_interact_id"))
            {
                int.TryParse(row["target_interact_id"], out targetInteractId);
            }

            var targetActionId = 0;
            if (row.ContainsKey("target_action_id"))
            {
                int.TryParse(row["target_action_id"], out targetActionId);
            }

            return new AreaRuleInfoData
            {
                Id = int.Parse(row["id"]),
                AreaType = (AreaType)int.Parse(row["area_type"]),
                GroupId = int.Parse(row["group_id"]),
                Description = row["description"],
                TargetInteractId = targetInteractId,
                TargetActionId = targetActionId
            };
        }
    }
}
