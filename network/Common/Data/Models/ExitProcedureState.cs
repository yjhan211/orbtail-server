// ReSharper disable All

#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.Linq;

namespace network.common.data.models
{
    /// <summary>
    ///     동적으로 생성된 탈출 절차 인스턴스
    /// </summary>
    public class ExitProcedureInstance
    {
        public int GroupId { get; set; }
        public List<ExitProcedureStep> Steps { get; set; } = new();
        public int CurrentStepIndex { get; set; }
        public DateTime CreatedAt { get; set; }

        public ExitProcedureStep? CurrentStep =>
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
    ///     탈출 절차의 각 단계
    /// </summary>
    public class ExitProcedureStep
    {
        public int StepOrder { get; set; }
        public ExitStepData StepData { get; set; }
        public bool IsCompleted { get; set; }

        /// <summary>
        ///     텍스트 반환
        /// </summary>
        public string GetFormattedText() => StepData.TextTemplate;

        public string GetFormattedInsanityText() => StepData.InsanityTextTemplate;

        public string GetFormattedSummary() => StepData.SummaryTemplate;
    }

    /// <summary>
    ///     탈출 절차 생성기
    /// </summary>
    public static class ExitProcedureGenerator
    {
        /// <summary>
        ///     그룹 ID를 기반으로 탈출 절차 인스턴스 생성
        /// </summary>
        public static ExitProcedureInstance Generate(int groupId)
        {
            var instance = new ExitProcedureInstance
            {
                GroupId = groupId, CurrentStepIndex = 0, CreatedAt = DateTime.UtcNow
            };

            // 그룹에서 스텝 정의 가져오기
            var stepDefinitions = GameExitData.GetStepsByGroup(groupId);

            foreach (var stepDef in stepDefinitions)
            {
                var step = new ExitProcedureStep { StepOrder = stepDef.StepOrder, StepData = stepDef };

                instance.Steps.Add(step);
            }

            return instance;
        }
    }
}
