// ReSharper disable All
#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.Linq;

namespace network.common.data.models
{
    /// <summary>
    /// 동적으로 생성된 탈출 절차 인스턴스
    /// </summary>
    public class ExitProcedureInstance
    {
        public int ScenarioId { get; set; }
        public ExitTemplateType TemplateType { get; set; }
        public List<ExitProcedureStep> Steps { get; set; } = new();
        public int CurrentStepIndex { get; set; }
        public ExitFinaleType FinaleType { get; set; }
        public ExitEffectType EffectType { get; set; }
        public DateTime CreatedAt { get; set; }

        public ExitProcedureStep CurrentStep =>
            CurrentStepIndex < Steps.Count ? Steps[CurrentStepIndex] : null;

        public bool IsCompleted => CurrentStepIndex >= Steps.Count;

        public bool AdvanceStep()
        {
            if (IsCompleted) return false;
            CurrentStepIndex++;
            return true;
        }
    }

    /// <summary>
    /// 탈출 절차의 각 단계
    /// </summary>
    public class ExitProcedureStep
    {
        public int StepOrder { get; set; }
        public ExitStepData StepData { get; set; }

        // 슬롯에 할당된 데이터
        public ExitItemData SelectedItem { get; set; }
        public ExitSpotData SelectedSpot { get; set; }
        public ExitDebuffData SelectedDebuff { get; set; }
        public ExitConditionData SelectedCondition { get; set; }

        public bool IsCompleted { get; set; }

        /// <summary>
        /// 플레이스홀더가 치환된 텍스트 반환
        /// </summary>
        public string GetFormattedText()
        {
            var context = BuildReplacementContext();
            return GameSystemTextData.FormatTemplate(StepData.TextTemplate, context);
        }

        public string GetFormattedInsanityText()
        {
            var context = BuildReplacementContext();
            return GameSystemTextData.FormatTemplate(StepData.InsanityTextTemplate, context);
        }

        public string GetFormattedSummary()
        {
            var context = BuildReplacementContext();
            return GameSystemTextData.FormatTemplate(StepData.SummaryTemplate, context);
        }

        private TextReplacementContext BuildReplacementContext()
        {
            var context = TextReplacementContext.Create();

            if (SelectedItem != null)
            {
                var itemInfo = GameItemData.Get(SelectedItem.ItemId);
                var areaName = GetItemSpawnAreaName(SelectedItem.ItemId);
                context.WithItem(itemInfo?.Name?.Kr ?? "???", areaName, SelectedItem.Warning);
            }

            if (SelectedSpot != null)
            {
                var interactable = GameInteractableData.Get(SelectedSpot.InteractableId);
                context.WithSpot(interactable?.ShortName ?? "???");
            }

            if (SelectedDebuff != null)
            {
                context.WithDebuff(SelectedDebuff.Warning);
            }

            if (SelectedCondition != null)
            {
                context.WithCondition(SelectedCondition.Text);
            }

            return context;
        }

        private string GetItemSpawnAreaName(int itemId)
        {
            var interactableId = GameInteractableData.GetInteractableIdByRewardItemId(itemId);
            if (interactableId.HasValue)
            {
                var interactable = GameInteractableData.Get(interactableId.Value);
                if (interactable != null)
                {
                    return GameAreaNameData.Get((AreaType)interactable.ZoneId);
                }
            }
            return "???";
        }
    }

    /// <summary>
    /// 탈출 절차 생성기
    /// </summary>
    public static class ExitProcedureGenerator
    {
        private static readonly Random _random = new();

        /// <summary>
        /// 시나리오 데이터를 기반으로 탈출 절차 인스턴스 생성
        /// </summary>
        public static ExitProcedureInstance Generate(ExitTemplateType templateType, int? seed = null)
        {
            var scenario = GameExitScenarioData.GetByTemplateType(templateType);
            if (scenario == null)
                throw new InvalidOperationException($"Scenario not found for template type: {templateType}");

            var random = seed.HasValue ? new Random(seed.Value) : _random;

            var instance = new ExitProcedureInstance
            {
                ScenarioId = scenario.Id,
                TemplateType = templateType,
                FinaleType = scenario.FinaleType,
                EffectType = scenario.EffectType,
                CurrentStepIndex = 0,
                CreatedAt = DateTime.UtcNow
            };

            // 템플릿의 스텝 정의 가져오기
            var stepDefinitions = GameExitData.GetStepsByTemplate((int)templateType);

            // 풀에서 랜덤 선택
            var selectedItemIds = SelectFromIntPool(scenario.ItemIdPool, stepDefinitions.Count(s => s.ItemSlot), random);
            var selectedSpots = SelectFromPool(scenario.SpotTypePool, stepDefinitions.Count(s => s.SpotSlot), random);
            var selectedDebuffs = SelectFromPool(scenario.DebuffTypePool, stepDefinitions.Count(s => s.DebuffSlot), random);
            var selectedConditions = SelectFromPool(scenario.ConditionTypePool, stepDefinitions.Count(s => s.ConditionSlot), random);

            int itemIdx = 0, spotIdx = 0, debuffIdx = 0, conditionIdx = 0;

            foreach (var stepDef in stepDefinitions.Take(scenario.StepCount))
            {
                var step = new ExitProcedureStep
                {
                    StepOrder = stepDef.StepOrder,
                    StepData = stepDef
                };

                // 슬롯에 데이터 할당
                if (stepDef.ItemSlot && itemIdx < selectedItemIds.Count)
                {
                    step.SelectedItem = GameExitData.GetItem(selectedItemIds[itemIdx++]);
                }
                if (stepDef.SpotSlot && spotIdx < selectedSpots.Count)
                {
                    step.SelectedSpot = GameExitData.GetSpot((int)selectedSpots[spotIdx++]);
                }
                if (stepDef.DebuffSlot && debuffIdx < selectedDebuffs.Count)
                {
                    step.SelectedDebuff = GameExitData.GetDebuff((int)selectedDebuffs[debuffIdx++]);
                }
                if (stepDef.ConditionSlot && conditionIdx < selectedConditions.Count)
                {
                    step.SelectedCondition = GameExitData.GetCondition((int)selectedConditions[conditionIdx++]);
                }

                instance.Steps.Add(step);
            }

            return instance;
        }

        private static List<int> SelectFromIntPool(List<int> pool, int count, Random random)
        {
            if (pool == null || pool.Count == 0 || count <= 0)
                return new List<int>();

            var result = new List<int>();
            var available = new List<int>(pool);

            for (int i = 0; i < count && available.Count > 0; i++)
            {
                var index = random.Next(available.Count);
                result.Add(available[index]);
            }

            return result;
        }

        private static List<T> SelectFromPool<T>(List<T> pool, int count, Random random) where T : struct, Enum
        {
            if (pool == null || pool.Count == 0 || count <= 0)
                return new List<T>();

            var result = new List<T>();
            var available = new List<T>(pool);

            for (int i = 0; i < count && available.Count > 0; i++)
            {
                var index = random.Next(available.Count);
                result.Add(available[index]);
            }

            return result;
        }

        /// <summary>
        /// 특정 시드로 동일한 절차 재생성 (테스트/디버그용)
        /// </summary>
        public static ExitProcedureInstance GenerateWithSeed(ExitTemplateType templateType, int seed)
        {
            return Generate(templateType, seed);
        }
    }
}
