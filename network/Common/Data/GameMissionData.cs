// ReSharper disable All
#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;
using network.managers;

namespace network.common.data
{
    /// <summary>
    ///     직책별 단계별 미션 데이터.
    ///     job_title → step_order 순서로 미션 진행.
    /// </summary>
    public static class GameMissionData
    {
        // JobTitle(short) → step_order 순 정렬된 미션 단계 목록
        private static readonly Dictionary<short, List<MissionStepData>> _stepsByJob = new();

        public static void Initialize(List<CsvRow> data)
        {
            _stepsByJob.Clear();
            foreach (var row in data)
            {
                short jobTitle = short.Parse(row["job_title"]);
                int stepOrder = int.Parse(row["step_order"]);

                var step = new MissionStepData
                {
                    JobTitle = jobTitle,
                    StepOrder = stepOrder,
                    TargetArea = int.Parse(row["target_area"]),
                    TargetInteractId = int.Parse(row["target_interact_id"]),
                    TargetActionId = int.Parse(row["target_action_id"]),
                    TraceDescription = row["trace_description_kr"],
                    StaminaReward = int.Parse(row["stamina_reward"])
                };

                if (!_stepsByJob.ContainsKey(jobTitle))
                    _stepsByJob[jobTitle] = new List<MissionStepData>();

                _stepsByJob[jobTitle].Add(step);
            }

            // 각 직책 내에서 step_order 정렬
            foreach (var steps in _stepsByJob.Values)
                steps.Sort((a, b) => a.StepOrder.CompareTo(b.StepOrder));
        }

        /// <summary>
        ///     해당 직책의 모든 미션 단계 반환
        /// </summary>
        public static List<MissionStepData> GetSteps(short jobTitle) =>
            _stepsByJob.GetValueOrDefault(jobTitle) ?? new List<MissionStepData>();

        /// <summary>
        ///     해당 직책의 특정 단계 반환
        /// </summary>
        public static MissionStepData GetStep(short jobTitle, int stepOrder)
        {
            var steps = GetSteps(jobTitle);
            return steps.FirstOrDefault(s => s.StepOrder == stepOrder);
        }

        /// <summary>
        ///     해당 직책의 총 미션 단계 수
        /// </summary>
        public static int GetTotalSteps(short jobTitle) => GetSteps(jobTitle).Count;

        public static void Validate(managers.LogManager logger)
        {
            if (_stepsByJob.Count == 0)
                throw new InvalidOperationException("미션 데이터가 로드되지 않았습니다");

            foreach (var (jobTitle, steps) in _stepsByJob)
            {
                if (steps.Count == 0)
                    LogManager.WriteInfoLog($"[GameMissionData] 직책 {jobTitle}에 미션 단계가 없습니다");
            }
        }
    }

    public class MissionStepData
    {
        public short JobTitle { get; set; }
        public int StepOrder { get; set; }
        public int TargetArea { get; set; }        // AreaType enum 값
        public int TargetInteractId { get; set; }
        public int TargetActionId { get; set; }
        public string TraceDescription { get; set; }  // 흔적 설명
        public int StaminaReward { get; set; }     // 완료 시 스태미나 보상
    }
}
